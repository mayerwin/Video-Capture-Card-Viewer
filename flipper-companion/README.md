# VCKVM Bridge — Flipper Zero companion app

This optional app turns a **Flipper Zero** into the USB‑HID dongle for the Video Capture Card
Viewer's KVM feature. The viewer sends keyboard/mouse commands over a serial link; the Flipper
re‑emits them as a real USB keyboard + mouse to the **target** machine.

```
[PC: Video Capture Card Viewer]  --Bluetooth LE-->  [Flipper Zero]  --USB HID-->  [target machine]
        (paired; writes the Flipper serial GATT)              (USB-C to the target)
```

**Primary path = Bluetooth.** The viewer's **"Flipper Zero (Bluetooth)"** backend pairs with the
Flipper and writes this protocol straight to its custom serial GATT service — no COM bridge, no extra
dongle. The Flipper's USB stays free to be the HID device into the target. (USB and BLE run on
different cores, so they coexist.) A GPIO-UART wire is supported as a fallback.

> **Reality check.** A Flipper is *not* required — a **CH9329** dongle (~$10) is the simplest,
> fully‑tested backend and needs no custom firmware. The Flipper path needs this companion app and is
> best-effort against the official firmware's BLE-serial API (verify on-device); its USB HID mouse is
> *relative only*, so absolute coordinates are approximated. One radio serves one host: USB drives the
> target, so commands arrive over **Bluetooth** (or the GPIO UART), never the same USB cable.

## Protocol

ASCII lines, `\n`‑terminated, `115200 8N1` (emitted by the `FlipperZeroBackend` class in
[`../Kvm/SerialKvmBackend.cs`](../Kvm/SerialKvmBackend.cs); set the viewer's KVM baud to **115200**
to match this firmware):

| Line | Meaning |
|------|---------|
| `VCKVM 1` | handshake (ignored by the app) |
| `KB m k1 k2 k3 k4 k5 k6` | 8 hex bytes — modifiers + up to 6 HID usage IDs (`00` = empty slot) |
| `MA x y buttons wheel` | absolute pointer; `x`,`y` = `0..32767`, `buttons` bitmask, `wheel` signed |
| `MR dx dy buttons wheel` | relative pointer; signed deltas |

HID mouse buttons: `1`=left, `2`=right, `4`=middle. HID modifier bits: `0x01` LCtrl, `0x02` LShift,
`0x04` LAlt, `0x08` LGUI, `0x10` RCtrl, `0x20` RShift, `0x40` RAlt, `0x80` RGUI.

## Build & install

1. Install the Flipper build tool: `pip install ufbt` then `ufbt update`.
2. From this folder: `ufbt` (builds `dist/vckvm_bridge.fap`).
3. Deploy with the Flipper plugged in via USB: `ufbt launch` — or copy the `.fap` to
   `SD/apps/USB/` and run it from **Apps → USB → VCKVM Bridge**.

When the app starts it switches the Flipper's USB into HID mode. Plug that USB‑C into the **target**.

## Connecting

- **Bluetooth (primary):** pair the Flipper with the PC in Windows Bluetooth settings. Run **VCKVM
  Bridge** on the Flipper, then in the viewer's Settings choose **KVM → Flipper Zero (Bluetooth)**,
  pick the paired Flipper, and **Connect**. The app talks to the Flipper's serial GATT service
  directly (service `8fe5b3d5-…`, write characteristic `…62fe0000`). The firmware registers a BLE
  serial RX callback (`furi_hal_bt_serial_set_event_callback`) that feeds the same parser.
- **GPIO UART (fallback):** feed lines into pins **13 (RX)** / **14 (TX)**, `115200 8N1`, from a
  USB‑UART adapter, and choose **Flipper Zero (USB / serial)** with that COM port. Both inputs feed
  the same buffer, so either works.

## Pointer sensitivity

The Flipper's USB mouse is relative-only, so absolute coordinates are converted to relative deltas in
`vckvm_bridge.c` using a `/16` divisor (remainder carried, so there's no drift). Smaller divisor =
faster pointer. If the cursor under/over-shoots on your target, tune that divisor and/or the target's
pointer speed/acceleration. A keepalive isn't required — a quiet link for 500 ms triggers a one-shot
release-all so nothing stays stuck.

## Notes for porting

`furi_hal_*` names occasionally change between firmware versions. The firmware‑specific calls are the
HID emit functions (`furi_hal_hid_kb_press/release`, `furi_hal_hid_mouse_move/press/release/scroll`),
the **BLE serial RX** (`furi_hal_bt_serial_set_event_callback` + `SerialServiceEvent`), and the GPIO
UART RX. If the build can't resolve a symbol, check your installed SDK headers (or the
`ble_profile_serial_*` API on newer firmware) and adjust those few calls.
