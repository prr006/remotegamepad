package com.example.remotegamepad

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.view.View
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions

/**
 * Screen 1 - Connection.
 *
 * This is the app's launcher screen and the ONLY place connection setup
 * lives. It offers two connection methods (Wi-Fi QR pairing, Bluetooth),
 * shows live connection status, and lets the user switch between methods
 * freely. On a successful connection it hands the resulting
 * [GamepadTransport] to [ConnectionSession] and transitions automatically
 * to [GamepadActivity], which contains none of this setup UI.
 */
class ConnectionActivity : AppCompatActivity() {

    private lateinit var tabWifi: TextView
    private lateinit var tabBluetooth: TextView
    private lateinit var wifiPanel: View
    private lateinit var btPanel: View
    private lateinit var tvStatus: TextView
    private lateinit var connectionDot: View
    private lateinit var btDeviceContainer: LinearLayout
    private lateinit var tvBtHint: TextView

    private var connecting = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_connection)

        // If a transport from an earlier session is still alive (e.g. the
        // user backed out of the gamepad screen without disconnecting),
        // skip setup and go straight back in.
        if (ConnectionSession.transport?.isReady() == true) {
            goToGamepad()
            return
        }

        tabWifi = findViewById(R.id.tabWifi)
        tabBluetooth = findViewById(R.id.tabBluetooth)
        wifiPanel = findViewById(R.id.wifiPanel)
        btPanel = findViewById(R.id.btPanel)
        tvStatus = findViewById(R.id.tvConnStatus)
        connectionDot = findViewById(R.id.connStatusDot)
        btDeviceContainer = findViewById(R.id.btDeviceContainer)
        tvBtHint = findViewById(R.id.tvBtHint)

        tabWifi.setOnClickListener { showWifiPanel() }
        tabBluetooth.setOnClickListener { showBluetoothPanel() }

        findViewById<View>(R.id.btnScanQr).setOnClickListener { launchScanner() }
        findViewById<View>(R.id.btnEnableBluetooth).setOnClickListener { requestEnableBluetooth() }
        findViewById<View>(R.id.btnRefreshDevices).setOnClickListener { refreshPairedDevices() }

        showWifiPanel()
        setStatus(connected = false, text = getString(R.string.status_disconnected))
    }

    override fun onResume() {
        super.onResume()
        if (btPanel.visibility == View.VISIBLE) refreshPairedDevices()
    }

    // ===================== METHOD SWITCHING =====================

    private fun showWifiPanel() {
        tabWifi.isSelected = true
        tabBluetooth.isSelected = false
        wifiPanel.visibility = View.VISIBLE
        btPanel.visibility = View.GONE
    }

    private fun showBluetoothPanel() {
        tabWifi.isSelected = false
        tabBluetooth.isSelected = true
        wifiPanel.visibility = View.GONE
        btPanel.visibility = View.VISIBLE
        refreshPairedDevices()
    }

    // ===================== WI-FI (QR PAIRING) =====================

    private fun launchScanner() {
        val options = ScanOptions()
        options.setPrompt(getString(R.string.qr_scan_prompt))
        options.setBeepEnabled(true)
        options.setOrientationLocked(false)
        barcodeLauncher.launch(options)
    }

    private val barcodeLauncher =
        registerForActivityResult(ScanContract()) { result ->
            val contents = result.contents
            if (contents == null) return@registerForActivityResult
            try {
                val parts = contents.split("|")
                if (parts.size >= 3 && parts[0] == "GAMEPAD") {
                    connectWifi(parts[1], parts[2].toInt())
                } else {
                    setStatus(connected = false, text = getString(R.string.status_failed))
                }
            } catch (e: Exception) {
                e.printStackTrace()
                setStatus(connected = false, text = getString(R.string.status_failed))
            }
        }

    private fun connectWifi(ip: String, port: Int) {
        if (connecting) return
        connecting = true
        setStatus(connected = false, text = getString(R.string.status_connecting))

        // Existing SocketClient / UDP connect path, completely unchanged -
        // only where it's called from has moved.
        val client = SocketClient(this)
        client.onConnectionChanged = { connected ->
            runOnUiThread {
                connecting = false
                if (connected) {
                    ConnectionSession.set(client, ConnectionMode.WIFI)
                    setStatus(connected = true, text = getString(R.string.status_connected))
                    goToGamepad()
                } else {
                    setStatus(connected = false, text = getString(R.string.status_failed))
                }
            }
        }
        client.connect(ip, port)
    }

    // ===================== BLUETOOTH =====================

    private fun bluetoothAdapter(): BluetoothAdapter? =
        getSystemService(BluetoothManager::class.java)?.adapter

    private fun hasBluetoothConnectPermission(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) return true
        return ContextCompat.checkSelfPermission(
            this, android.Manifest.permission.BLUETOOTH_CONNECT
        ) == PackageManager.PERMISSION_GRANTED
    }

    // BLUETOOTH_SCAN is required (API 31+) for BluetoothAdapter.cancelDiscovery(),
    // which BluetoothClient.connect() calls before opening the RFCOMM socket.
    // Missing this was the confirmed cause of the SecurityException.
    private fun hasBluetoothScanPermission(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) return true
        return ContextCompat.checkSelfPermission(
            this, android.Manifest.permission.BLUETOOTH_SCAN
        ) == PackageManager.PERMISSION_GRANTED
    }

    private fun hasRequiredBluetoothPermissions(): Boolean =
        hasBluetoothConnectPermission() && hasBluetoothScanPermission()

    private fun requiredBluetoothPermissions(): Array<String> =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            arrayOf(
                android.Manifest.permission.BLUETOOTH_CONNECT,
                android.Manifest.permission.BLUETOOTH_SCAN
            )
        } else {
            emptyArray()
        }

    private val requestBtPermissions =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { results ->
            if (results.values.all { it }) refreshPairedDevices()
            else Toast.makeText(this, R.string.bt_permission_denied, Toast.LENGTH_SHORT).show()
        }

    private val enableBtLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
            refreshPairedDevices()
        }

    private fun requestEnableBluetooth() {
        val adapter = bluetoothAdapter()
        if (adapter == null) {
            Toast.makeText(this, R.string.bt_not_supported, Toast.LENGTH_SHORT).show()
            return
        }
        if (!hasRequiredBluetoothPermissions()) {
            requestBtPermissions.launch(requiredBluetoothPermissions())
            return
        }
        if (!adapter.isEnabled) {
            enableBtLauncher.launch(Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE))
        } else {
            refreshPairedDevices()
        }
    }

    @SuppressLint("MissingPermission")
    private fun refreshPairedDevices() {
        val adapter = bluetoothAdapter()
        btDeviceContainer.removeAllViews()

        if (adapter == null) {
            showBtHint(getString(R.string.bt_not_supported))
            return
        }
        if (!hasRequiredBluetoothPermissions()) {
            showBtHint(getString(R.string.bt_permission_hint))
            return
        }
        if (!adapter.isEnabled) {
            showBtHint(getString(R.string.bt_disabled_hint))
            return
        }

        val paired: Set<BluetoothDevice> = try {
            adapter.bondedDevices
        } catch (e: SecurityException) {
            emptySet()
        }

        if (paired.isEmpty()) {
            showBtHint(getString(R.string.bt_no_paired))
            return
        }

        tvBtHint.visibility = View.GONE
        for (device in paired) {
            val row = layoutInflater.inflate(R.layout.item_bt_device, btDeviceContainer, false)
            row.findViewById<TextView>(R.id.tvDeviceName).text = device.name ?: device.address
            row.findViewById<TextView>(R.id.tvDeviceAddress).text = device.address
            row.setOnClickListener { connectBluetooth(device) }
            btDeviceContainer.addView(row)
        }
    }

    private fun showBtHint(text: String) {
        tvBtHint.text = text
        tvBtHint.visibility = View.VISIBLE
    }

    @SuppressLint("MissingPermission")
    private fun connectBluetooth(device: BluetoothDevice) {
        if (connecting) return
        // Guard immediately before entering the connect path: BluetoothClient.connect()
        // calls cancelDiscovery() as its first action, which requires BLUETOOTH_SCAN.
        // Re-checked here (not just in refreshPairedDevices) in case a permission was
        // revoked between the list refresh and this tap.
        if (!hasRequiredBluetoothPermissions()) {
            Toast.makeText(this, R.string.bt_permission_denied, Toast.LENGTH_SHORT).show()
            requestBtPermissions.launch(requiredBluetoothPermissions())
            return
        }
        connecting = true
        setStatus(connected = false, text = getString(R.string.status_connecting))

        val client = BluetoothClient()
        client.onConnectionChanged = { connected ->
            if (!connected) runOnUiThread { setStatus(connected = false, text = getString(R.string.status_failed)) }
        }
        client.connect(device) { success ->
            runOnUiThread {
                connecting = false
                if (success) {
                    ConnectionSession.set(client, ConnectionMode.BLUETOOTH)
                    setStatus(connected = true, text = getString(R.string.status_connected))
                    goToGamepad()
                } else {
                    setStatus(connected = false, text = getString(R.string.status_failed))
                    // TEMPORARY DIAGNOSTIC: show the full exception class, message,
                    // and first few stack frames so the exact failing API call is
                    // visible without adb/Logcat. Read-only - does not change any
                    // Bluetooth connection logic.
                    val detail = client.lastError
                    if (detail != null) {
                        androidx.appcompat.app.AlertDialog.Builder(this)
                            .setTitle(getString(R.string.bt_connect_failed))
                            .setMessage(detail)
                            .setPositiveButton(android.R.string.ok, null)
                            .setCancelable(true)
                            .create()
                            .also { dialog ->
                                dialog.setOnShowListener {
                                    dialog.findViewById<TextView>(android.R.id.message)
                                        ?.setTextIsSelectable(true)
                                }
                            }
                            .show()
                    } else {
                        Toast.makeText(this, R.string.bt_connect_failed, Toast.LENGTH_LONG).show()
                    }
                }
            }
        }
    }

    // ===================== SHARED =====================

    private fun setStatus(connected: Boolean, text: String) {
        tvStatus.text = text
        connectionDot.setBackgroundResource(
            if (connected) R.drawable.dot_connected else R.drawable.dot_disconnected
        )
    }

    private fun goToGamepad() {
        startActivity(Intent(this, GamepadActivity::class.java))
        finish()
    }
}