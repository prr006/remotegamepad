#!/bin/bash
set -e
cd "$(dirname "$0")/../Android"
./gradlew assembleDebug
echo "APK built at: app/build/outputs/apk/debug/app-debug.apk"
