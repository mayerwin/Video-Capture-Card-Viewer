# VCKVM Bridge — Flipper Zero companion app

This optional app turns a **Flipper Zero** into the USB‑HID dongle for the Video Capture Card
Viewer's KVM feature. The viewer sends keyboard/mouse commands over a serial link; the Flipper
re‑emits them as a real USB keyboard + mouse to the **target** machine.

```
[PC: Video Capture Card Viewer]  --serial-->  [Flipper Zero]  --USB HID-->  [target machine]
            (BLE UART, or GPIO UART pins 13/14)         (USB-C to the target)
```

> **Reality check.** A Flipper is *not* required — a **CH9329** dongle (~$10) is the recommended,
> fully‑tested backend and needs no custom firmware. The Flipper path is **experimental**: it needs
> this companion app, its USB HID mouse is *relative only* (absolute coordinates are approximated),
> and one radio serves one host at a time (USB → target, so commands must come in over BLE or the
> GPIO UART, **not** the same USB cable).

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

## Wiring the serial link

- **GPIO UART (implemented, most reliable):** feed lines into pins **13 (RX)** / **14 (TX)**,
  `115200 8N1`, from a USB‑UART adapter on the PC. Point the viewer's KVM COM port at that adapter.
  This is the path `vckvm_bridge.c` ships with. If the UART can't be acquired the app exits with an
  error (it won't hang silently).
- **Bluetooth (not wired up in this reference):** the user's intended PC→BLE→Flipper→USB topology is
  possible but needs you to feed the Flipper's BLE‑serial RX into the `g_rx` stream buffer (replace
  the USART acquire/callback in `vckvm_bridge.c` with your firmware's `furi_hal_bt` serial callback).
  Until you do, use the GPIO‑UART path above (a BLE‑UART→COM dongle on the PC side also works).

## Notes for porting

`furi_hal_*` names occasionally change between firmware versions. The only firmware‑specific calls are
the HID emit functions (`furi_hal_hid_kb_press/release`, `furi_hal_hid_mouse_move/press/release/scroll`)
and the serial RX in `vckvm_bridge.c`. If the build can't resolve a symbol, check your installed SDK
headers and adjust those few calls.
