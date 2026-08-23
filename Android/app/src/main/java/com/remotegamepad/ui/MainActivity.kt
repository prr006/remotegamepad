package com.remotegamepad.ui

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
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
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class MainActivity : AppCompatActivity(), BluetoothService.Callback, GamepadView.Listener {

    companion object {
        private const val TAG = "MainActivity"
    }

    private lateinit var binding: ActivityMainBinding
    private lateinit var bluetoothService: BluetoothService
    private val bluetoothAdapter: BluetoothAdapter? by lazy {
        (getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter
    }

    private val pairedAdapter = DeviceAdapter { device -> connectTo(device) }
    private val discoveredAdapter = DeviceAdapter { device -> connectTo(device) }
    private val discoveredDevices = mutableListOf<BluetoothDevice>()
    private val sdf = SimpleDateFormat("HH:mm:ss.SSS", Locale.getDefault())
    private var lastSentState: GamepadState? = null

    private var isScanning = false

    // Permission launcher
    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { permissions ->
        val allGranted = permissions.entries.all { it.value }
        if (allGranted) {
            log("Permissions granted")
            loadPairedDevices()
        } else {
            log("Permissions denied — Bluetooth unavailable")
            AlertDialog.Builder(this)
                .setTitle("Permissions Required")
                .setMessage("Bluetooth and location permissions are needed to discover and connect to the Windows server.")
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
                            log("Discovered: ${it.name} [${it.address}]")
                        }
                    }
                }
                BluetoothAdapter.ACTION_DISCOVERY_STARTED -> {
                    isScanning = true
                    binding.btnScan.text = getString(com.remotegamepad.R.string.btn_stop_scan)
                    log("Discovery started")
                }
                BluetoothAdapter.ACTION_DISCOVERY_FINISHED -> {
                    isScanning = false
                    binding.btnScan.text = getString(com.remotegamepad.R.string.btn_scan)
                    log("Discovery finished")
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

        binding.btnDisconnect.setOnClickListener {
            log("Disconnecting...")
            bluetoothService.disconnect()
        }

        binding.btnSendHello.setOnClickListener {
            if (bluetoothService.send(Protocol.MSG_HELLO)) {
                log("→ Sent: HELLO")
            } else {
                log("× Failed to send HELLO")
            }
        }

        binding.btnSendPing.setOnClickListener {
            if (bluetoothService.send(Protocol.MSG_PING)) {
                log("→ Sent: PING")
            } else {
                log("× Failed to send PING")
            }
        }
    }

    private fun setupGamepad() {
        binding.gamepadView.listener = this
    }
    //endregion

    //region GamepadView.Listener
    override fun onStateChanged(state: GamepadState) {
        if (!bluetoothService.isConnected()) return
        // Only send when state actually changed to avoid flooding
        // the Bluetooth channel with redundant stick-move messages.
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
        log("Loaded ${paired.size} paired device(s)")
    }

    private fun startDiscovery() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_SCAN)
            != PackageManager.PERMISSION_GRANTED) {
            log("BLUETOOTH_SCAN permission missing")
            return
        }
        discoveredDevices.clear()
        discoveredAdapter.submitList(emptyList())
        bluetoothAdapter?.cancelDiscovery()
        val started = bluetoothAdapter?.startDiscovery() ?: false
        if (!started) {
            log("Failed to start discovery")
        }
    }

    private fun stopDiscovery() {
        bluetoothAdapter?.cancelDiscovery()
    }
    //endregion

    //region Connection
    private fun connectTo(device: BluetoothDevice) {
        if (isScanning) stopDiscovery()
        log("Connecting to ${device.name ?: "Unknown"} [${device.address}]...")
        bluetoothService.connect(device)
    }
    //endregion

    //region BluetoothService.Callback
    override fun onStateChanged(state: BluetoothService.State) {
        when (state) {
            is BluetoothService.State.Disconnected -> {
                binding.tvStatus.text = getString(com.remotegamepad.R.string.status_disconnected)
                binding.statusIndicator.setBackgroundResource(com.remotegamepad.R.drawable.circle_red)
                binding.btnDisconnect.visibility = View.GONE
                binding.btnSendHello.isEnabled = false
                binding.btnSendPing.isEnabled = false
                binding.layoutConnection.visibility = View.VISIBLE
                binding.gamepadView.visibility = View.GONE
                log("State: Disconnected")
            }
            is BluetoothService.State.Connecting -> {
                binding.tvStatus.text = getString(com.remotegamepad.R.string.status_connecting)
                binding.statusIndicator.setBackgroundResource(com.remotegamepad.R.drawable.circle_orange)
                binding.btnDisconnect.visibility = View.VISIBLE
                binding.btnSendHello.isEnabled = false
                binding.btnSendPing.isEnabled = false
                binding.layoutConnection.visibility = View.VISIBLE
                binding.gamepadView.visibility = View.GONE
                log("State: Connecting")
            }
            is BluetoothService.State.Connected -> {
                binding.tvStatus.text = getString(com.remotegamepad.R.string.controller_connected)
                binding.statusIndicator.setBackgroundResource(com.remotegamepad.R.drawable.circle_green)
                binding.btnDisconnect.visibility = View.VISIBLE
                binding.btnSendHello.isEnabled = true
                binding.btnSendPing.isEnabled = true
                binding.layoutConnection.visibility = View.GONE
                binding.gamepadView.visibility = View.VISIBLE
                log("State: Connected — Controller active")
            }
            is BluetoothService.State.Error -> {
                binding.tvStatus.text = "Error: ${state.reason}"
                binding.statusIndicator.setBackgroundResource(com.remotegamepad.R.drawable.circle_red)
                binding.btnDisconnect.visibility = View.GONE
                binding.btnSendHello.isEnabled = false
                binding.btnSendPing.isEnabled = false
                binding.layoutConnection.visibility = View.VISIBLE
                binding.gamepadView.visibility = View.GONE
                log("State: Error — ${state.reason}")
            }
        }
    }

    override fun onMessageReceived(message: String) {
        log("← Received: $message")
    }

    override fun onError(message: String) {
        log("! Error: $message")
    }
    //endregion

    //region Logging
    private fun log(msg: String) {
        val line = "[${sdf.format(Date())}] $msg\n"
        Log.d(TAG, msg)
        binding.tvLog.append(line)
        binding.scrollView.post {
            binding.scrollView.fullScroll(View.FOCUS_DOWN)
        }
    }
    //endregion

    //region DeviceAdapter
    class DeviceAdapter(private val onClick: (BluetoothDevice) -> Unit) :
        RecyclerView.Adapter<DeviceAdapter.ViewHolder>() {

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
            private val tvAddress: TextView = itemView.findViewById(com.remotegamepad.R.id.tvDeviceAddress)

            fun bind(device: BluetoothDevice) {
                tvName.text = device.name ?: "Unknown"
                tvAddress.text = device.address
                itemView.setOnClickListener { onClick(device) }
            }
        }
    }
    //endregion
}
