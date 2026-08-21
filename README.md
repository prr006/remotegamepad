# RemoteGamepad

RemoteGamepad turns an Android phone into a wireless Xbox-style controller for PC gaming.

The application communicates with a C# server over UDP and uses ViGEm to emulate a virtual Xbox 360 controller on Windows. Devices can be paired instantly using QR code scanning, eliminating the need for manual IP configuration.

## Features

* Wireless gamepad using Wi-Fi
* QR code based device pairing
* UDP low-latency communication
* Dual analog sticks
* D-Pad support
* Face buttons (A, B, X, Y)
* Triggers and shoulder buttons
* Start, Select, Home, and stick buttons
* Virtual Xbox 360 controller emulation using ViGEm
* Support for multiple connected controllers

## Tech Stack

### Android Client

* Kotlin
* Android SDK
* ZXing Barcode Scanner

### PC Server

* C#
* .NET
* ViGEmBus
* QRCoder

## Architecture

```text
Android Phone
      ↓ UDP
C# Server
      ↓ ViGEm
Virtual Xbox Controller
      ↓
PC Games
```

## Installation

### Android App

1. Clone the repository

```bash
git clone https://github.com/rahulp06/RemoteGamepad.git
```

2. Open the project in Android Studio
3. Sync Gradle dependencies
4. Build and install the APK

### Server

1. Install .NET SDK
2. Install ViGEmBus
3. Build and run the server
4. Scan the generated QR code from the Android app

## Future Improvements

* PlayStation controller theme
* Multiple controller skins
* Controller vibration support
* Custom button mapping
* Bluetooth connectivity
* Improved analog stick precision

## Author

Rahul P

## License

This project is intended for learning and educational purposes.
