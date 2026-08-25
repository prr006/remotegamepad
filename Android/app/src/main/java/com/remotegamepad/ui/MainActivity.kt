package com.remotegamepad.ui

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import android.widget.Toast
import androidx.activity.OnBackPressedCallback
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.remotegamepad.bluetooth.BluetoothService
import com.remotegamepad.bluetooth.GamepadState
import com.remotegamepad.bluetooth.Protocol
import com.remotegamepad.databinding.ActivityMainBinding

class MainActivity : AppCompatActivity(), BluetoothService.Callback, GamepadView.Listener {

    companion object {
        private const val TAG = "MainActivity"
        private const val PREFS_NAME = "remote_gamepad_prefs"
        private const val KEY_CONTROLLER_STYLE = "controller_style"
    }

    // ── Screen navigation ──
    private enum class Screen { HOME, CONNECT, GAMEPAD }
    private var currentScreen = Screen.HOME

    private lateinit var binding: ActivityMainBinding
    private lateinit var bluetoothService: BluetoothService
    private val bluetoothAdapter: BluetoothAdapter? by lazy {
        (getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter
    }

    private val pairedAdapter = DeviceAdapter(showPairedBadge = true) { device -> connectTo(device) }
    private val discoveredAdapter = DeviceAdapter(showPairedBadge = false) { device -> connectTo(device) }
    private val discoveredDevices = mutableListOf<BluetoothDevice>()
    private var lastSentState: GamepadState? = null

    private var isScanning = false
    private var connectingDeviceName: String? = null

    // Permission launcher
    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { permissions ->
        val allGranted = permissions.entries.all { it.value }
        if (allGranted) {
            Log.d(TAG, "Permissions granted")
            loadPairedDevices()
        } else {
            Log.w(TAG, "Permissions denied")
            AlertDialog.Builder(this)
                .setTitle("Permissions Required")
                .setMessage("Bluetooth and location permissions are needed to discover and connect to your PC.")
                .setPositiveButton("OK") { _, _ -> }
                .show()
        }
    }

    // Discovery broadcast receiver
    private val discoveryReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            when (intent?.action) {
                BluetoothDevice.ACTION_FOUND -> {
                    val device: BluetoothDevice? =
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                            intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE, BluetoothDevice::class.java)
                        } else {
                            @Suppress("DEPRECATION")
                            intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE)
                        }
                    device?.let {
                        if (it.name != null && discoveredDevices.none { d -> d.address == it.address }) {
                            discoveredDevices.add(it)
                            discoveredAdapter.submitList(discoveredDevices.toList())
                            updateEmptyState()
                            Log.d(TAG, "Discovered: ${it.name}")
                        }
                    }
                }
                BluetoothAdapter.ACTION_DISCOVERY_STARTED -> {
                    isScanning = true
                    binding.btnScan.text = getString(com.remotegamepad.R.string.btn_stop_scan)
                }
                BluetoothAdapter.ACTION_DISCOVERY_FINISHED -> {
                    isScanning = false
                    binding.btnScan.text = getString(com.remotegamepad.R.string.btn_scan)
                }
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        bluetoothService = BluetoothService(this)
        bluetoothService.setCallback(this)

        setupRecyclerViews()
        setupButtons()
        setupGamepad()
        setupNavigation()
        setupControllerStyle()
        registerReceivers()
        checkPermissions()
    }

    override fun onDestroy() {
        super.onDestroy()
        unregisterReceiver(discoveryReceiver)
        if (isScanning) {
            bluetoothAdapter?.cancelDiscovery()
        }
        bluetoothService.release()
    }

    //region UI Setup
    private fun setupRecyclerViews() {
        binding.rvPairedDevices.layoutManager = LinearLayoutManager(this)
        binding.rvPairedDevices.adapter = pairedAdapter

        binding.rvDiscoveredDevices.layoutManager = LinearLayoutManager(this)
        binding.rvDiscoveredDevices.adapter = discoveredAdapter
    }

    private fun setupButtons() {
        binding.btnScan.setOnClickListener {
            if (isScanning) {
                stopDiscovery()
            } else {
                startDiscovery()
            }
        }
    }

    private fun setupGamepad() {
        binding.gamepadView.listener = this
        // Restore persisted controller style
        val savedStyle = getSharedPreferences(PREFS_NAME, MODE_PRIVATE)
            .getString(KEY_CONTROLLER_STYLE, GamepadView.ControllerStyle.XBOX.name)
        binding.gamepadView.controllerStyle = try {
            GamepadView.ControllerStyle.valueOf(savedStyle!!)
        } catch (_: Exception) {
            GamepadView.ControllerStyle.XBOX
        }
    }

    private fun setupControllerStyle() {
        binding.btnControllerStyle.setOnClickListener {
            showControllerStyleDialog()
        }
    }

    private fun showControllerStyleDialog() {
        val styles = GamepadView.ControllerStyle.values()
        val labels = styles.map { style ->
            when (style) {
                GamepadView.ControllerStyle.XBOX -> getString(com.remotegamepad.R.string.style_xbox)
                GamepadView.ControllerStyle.PS -> getString(com.remotegamepad.R.string.style_ps)
            }
        }.toTypedArray()
        val currentIndex = styles.indexOf(binding.gamepadView.controllerStyle)

        AlertDialog.Builder(this)
            .setTitle(com.remotegamepad.R.string.controller_style_title)
            .setSingleChoiceItems(labels, currentIndex) { dialog: android.content.DialogInterface, which: Int ->
                val selected = styles[which]
                binding.gamepadView.controllerStyle = selected
                getSharedPreferences(PREFS_NAME, MODE_PRIVATE)
                    .edit()
                    .putString(KEY_CONTROLLER_STYLE, selected.name)
                    .apply()
                dialog.dismiss()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun setupNavigation() {
        // Home → Bluetooth connector
        binding.cardBluetooth.setOnClickListener {
            showConnect()
        }

        // Connector screen back arrow
        binding.btnBack.setOnClickListener {
            if (isScanning) stopDiscovery()
            bluetoothService.disconnect()
            showHome()
        }

        // System back button handling
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                when (currentScreen) {
                    Screen.GAMEPAD -> {
                        bluetoothService.disconnect()
                        showConnect()
                    }
                    Screen.CONNECT -> {
                        if (isScanning) stopDiscovery()
                        bluetoothService.disconnect()
                        showHome()
                    }
                    Screen.HOME -> {
                        finish()
                    }
                }
            }
        })
    }

    // ── Screen navigation ──

    private fun showHome() {
        currentScreen = Screen.HOME
        binding.layoutHome.visibility = View.VISIBLE
        binding.layoutConnection.visibility = View.GONE
        binding.gamepadView.visibility = View.GONE
        setGamepadOrientation(active = false)
    }

    private fun showConnect() {
        currentScreen = Screen.CONNECT
        binding.layoutHome.visibility = View.GONE
        binding.layoutConnection.visibility = View.VISIBLE
        binding.gamepadView.visibility = View.GONE
        setGamepadOrientation(active = false)
        updateEmptyState()
    }

    private fun showGamepad() {
        currentScreen = Screen.GAMEPAD
        binding.layoutHome.visibility = View.GONE
        binding.layoutConnection.visibility = View.GONE
        binding.gamepadView.visibility = View.VISIBLE
        setGamepadOrientation(active = true)
    }

    private fun setGamepadOrientation(active: Boolean) {
        requestedOrientation = if (active) {
            ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
        } else {
            ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        }
    }

    // ── Empty state management ──

    private fun updateEmptyState() {
        val hasPaired = pairedAdapter.itemCount > 0
        val hasDiscovered = discoveredAdapter.itemCount > 0
        val hasDevices = hasPaired || hasDiscovered

        binding.emptyState.visibility = if (hasDevices) View.GONE else View.VISIBLE
        binding.deviceListScroll.visibility = if (hasDevices) View.VISIBLE else View.GONE
        binding.pairedSectionHeader.visibility = if (hasPaired) View.VISIBLE else View.GONE
        binding.discoveredSectionHeader.visibility = if (hasDiscovered) View.VISIBLE else View.GONE
    }

    private fun showConnectingState(deviceName: String?) {
        connectingDeviceName = deviceName
        binding.connectingIndicator.visibility = View.VISIBLE
        binding.tvConnectingText.text = getString(com.remotegamepad.R.string.connecting_to_device)
    }

    private fun hideConnectingState() {
        connectingDeviceName = null
        binding.connectingIndicator.visibility = View.GONE
    }
    //endregion

    //region GamepadView.Listener
    override fun onStateChanged(state: GamepadState) {
        if (!bluetoothService.isConnected()) return
        if (lastSentState != state) {
            lastSentState = state
            bluetoothService.send(state.toProtocolMessage())
        }
    }
    //endregion

    //region Permissions
    private fun checkPermissions() {
        val permissions = mutableListOf<String>()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_SCAN)
                != PackageManager.PERMISSION_GRANTED) {
                permissions.add(Manifest.permission.BLUETOOTH_SCAN)
            }
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_CONNECT)
                != PackageManager.PERMISSION_GRANTED) {
                permissions.add(Manifest.permission.BLUETOOTH_CONNECT)
            }
        } else {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH)
                != PackageManager.PERMISSION_GRANTED) {
                permissions.add(Manifest.permission.BLUETOOTH)
            }
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_ADMIN)
                != PackageManager.PERMISSION_GRANTED) {
                permissions.add(Manifest.permission.BLUETOOTH_ADMIN)
            }
        }
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_FINE_LOCATION)
            != PackageManager.PERMISSION_GRANTED) {
            permissions.add(Manifest.permission.ACCESS_FINE_LOCATION)
        }

        if (permissions.isNotEmpty()) {
            permissionLauncher.launch(permissions.toTypedArray())
        } else {
            loadPairedDevices()
        }
    }
    //endregion

    //region Discovery
    private fun registerReceivers() {
        val filter = IntentFilter().apply {
            addAction(BluetoothDevice.ACTION_FOUND)
            addAction(BluetoothAdapter.ACTION_DISCOVERY_STARTED)
            addAction(BluetoothAdapter.ACTION_DISCOVERY_FINISHED)
        }
        registerReceiver(discoveryReceiver, filter)
    }

    private fun loadPairedDevices() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_CONNECT)
            != PackageManager.PERMISSION_GRANTED) {
            return
        }
        val paired = bluetoothAdapter?.bondedDevices?.toList() ?: emptyList()
        pairedAdapter.submitList(paired)
        updateEmptyState()
    }

    private fun startDiscovery() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_SCAN)
            != PackageManager.PERMISSION_GRANTED) {
            return
        }
        discoveredDevices.clear()
        discoveredAdapter.submitList(emptyList())
        updateEmptyState()
        bluetoothAdapter?.cancelDiscovery()
        val started = bluetoothAdapter?.startDiscovery() ?: false
        if (!started) {
            Log.w(TAG, "Failed to start discovery")
        }
    }

    private fun stopDiscovery() {
        bluetoothAdapter?.cancelDiscovery()
    }
    //endregion

    //region Connection
    private fun connectTo(device: BluetoothDevice) {
        if (isScanning) stopDiscovery()
        showConnectingState(device.name)
        bluetoothService.connect(device)
    }
    //endregion

    //region BluetoothService.Callback
    override fun onStateChanged(state: BluetoothService.State) {
        when (state) {
            is BluetoothService.State.Disconnected -> {
                hideConnectingState()
                if (currentScreen == Screen.GAMEPAD) {
                    showConnect()
                }
                Log.d(TAG, "State: Disconnected")
            }
            is BluetoothService.State.Connecting -> {
                Log.d(TAG, "State: Connecting")
            }
            is BluetoothService.State.Connected -> {
                hideConnectingState()
                showGamepad()
                Log.d(TAG, "State: Connected")
            }
            is BluetoothService.State.Error -> {
                hideConnectingState()
                if (currentScreen == Screen.GAMEPAD) {
                    showConnect()
                }
                Toast.makeText(this, getString(com.remotegamepad.R.string.connection_failed), Toast.LENGTH_SHORT).show()
                Log.d(TAG, "State: Error — ${state.reason}")
            }
        }
    }

    override fun onMessageReceived(message: String) {
        Log.d(TAG, "Received: $message")
    }

    override fun onError(message: String) {
        Log.e(TAG, "Error: $message")
    }
    //endregion

    //region DeviceAdapter
    class DeviceAdapter(
        private val showPairedBadge: Boolean = false,
        private val onClick: (BluetoothDevice) -> Unit
    ) : RecyclerView.Adapter<DeviceAdapter.ViewHolder>() {

        private var devices: List<BluetoothDevice> = emptyList()

        fun submitList(newList: List<BluetoothDevice>) {
            devices = newList
            notifyDataSetChanged()
        }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
            val view = LayoutInflater.from(parent.context)
                .inflate(com.remotegamepad.R.layout.item_device, parent, false)
            return ViewHolder(view)
        }

        override fun onBindViewHolder(holder: ViewHolder, position: Int) {
            holder.bind(devices[position])
        }

        override fun getItemCount(): Int = devices.size

        inner class ViewHolder(itemView: View) : RecyclerView.ViewHolder(itemView) {
            private val tvName: TextView = itemView.findViewById(com.remotegamepad.R.id.tvDeviceName)
            private val tvBadge: TextView = itemView.findViewById(com.remotegamepad.R.id.tvDeviceBadge)

            fun bind(device: BluetoothDevice) {
                tvName.text = device.name ?: "Unknown"
                tvBadge.visibility = if (showPairedBadge) View.VISIBLE else View.GONE
                itemView.setOnClickListener { onClick(device) }
            }
        }
    }
    //endregion
}
