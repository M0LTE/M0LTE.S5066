# Running s5066d on a Raspberry Pi with a CM108 interface

`s5066d` can drive a radio by itself: it plays and records the modem's audio through a USB sound card and keys the transmitter through the card's GPIO pin. You need nothing else on the Pi, no pdn-soundmodem and no KISS server. (If you already run pdn-soundmodem, its `s5066` section is the other way to run a node.)

This page covers Linux on a Raspberry Pi (Debian 13, arm64) with a CM108 or CM119 interface, the kind most home-built and Digirig-style interfaces use. The design is in `core-architecture.md` section 12.11.

## What you need

- A Pi running 64-bit Debian or Raspberry Pi OS.
- A CM108-family USB interface wired to the radio's data jack: audio out to the radio's data input, the radio's data output to audio in, and a GPIO pin (usually GPIO3) driving PTT.
- The ALSA library: `sudo apt install libasound2t64` (on older releases, `libasound2`). It is almost always there already.

## Install from the release .deb

Each GitHub release carries `s5066d` and `s5066-test` as `.deb` files for amd64 and arm64 (Debian 13, Raspberry Pi OS, Ubuntu 24.04). Download the two for your machine from the release page, then:

```
sudo apt install ./s5066d_VERSION_arm64.deb ./s5066-test_VERSION_arm64.deb
```

This gives you:

- `s5066d` and `s5066-test` on your PATH, with the .NET runtime built in;
- a `s5066d` system user in the `audio` group, and a udev rule that gives the `audio` group your CM108's hidraw node, so the sound card and PTT work without more setup;
- `/etc/s5066d/` with `example.json`, `radio-ft450.json` and `radio-ft818.json`, which an upgrade never overwrites if you edit them;
- a systemd unit that is **not** enabled, because it needs your configuration first.

To run a node, copy a config and start the service:

```
sudo cp /etc/s5066d/radio-ft450.json /etc/s5066d/s5066d.json
sudoedit /etc/s5066d/s5066d.json      # your station address, sound card and PTT device
sudo systemctl enable --now s5066d
journalctl -u s5066d -f
```

The service restarts after a sound card or interface failure, but gives up after five failed starts in two minutes, so a configuration error leaves it stopped with the reason in the journal; fix the file, then `sudo systemctl reset-failed s5066d` and start it again. The service can write only under `/var/lib/s5066d`, so point `rx_record` there.

Skip the build and permissions sections below; the package has done them. The packages are attached to the release only; they are not in an apt repository, so upgrade by installing the newer files.

## Build and copy it

On any machine with the .NET 10 SDK, from the repository:

```
dotnet publish src/s5066d -c Release -r linux-arm64 --self-contained -o s5066d-arm64
```

Copy the `s5066d-arm64` folder to the Pi. It carries its own .NET runtime, so the Pi needs nothing installed for it. (With .NET 10 installed on the Pi, you can leave out `--self-contained`.)

## Find the devices

Plug the interface in, then:

- **Sound card:** `arecord -l` and `aplay -l` list the cards. A CM108 usually shows as `USB Audio Device`, card name `Device`. Use `plughw:CARD=Device,DEV=0` for both capture and playback; naming the card rather than its number keeps it right when another USB card comes and goes.
- **PTT:** the CM108's hidraw node. `grep -il 0d8c /sys/class/hidraw/hidraw*/device/uevent` prints it, for example `/sys/class/hidraw/hidraw0/...`, which means `/dev/hidraw0`.

## Permissions

The user that runs `s5066d` needs the sound card and the hidraw node.

- Sound card: be in the `audio` group (`sudo usermod -aG audio $USER`, then log in again).
- hidraw: it belongs to root unless a rule says otherwise. Create `/etc/udev/rules.d/70-cm108.rules` with

  ```
  SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0d8c", GROUP="audio", MODE="0660"
  ```

  then `sudo udevadm control --reload && sudo udevadm trigger`, or unplug and replug the interface.

## Set the mixer

Run `alsamixer -c Device` and:

- turn **Auto Gain Control** off (F5 shows all controls). Left on, it pumps the receive level and spoils both decoding and the busy detector;
- set the capture (**Mic**) level so received noise sits well below full scale, and leave headroom for signals;
- set the playback (**Speaker**) level to about the middle, and set the final level with `tx_level` (below).

`sudo alsactl store` keeps the settings over a reboot.

## Configure

Start from `src/s5066d/radio-ft450.json` or `radio-ft818.json` and put in your device names. The radio part looks like this:

```
"modem": {
  "kind": "ms110d",
  "radio": {
    "capture_device": "plughw:CARD=Device,DEV=0",
    "playback_device": "plughw:CARD=Device,DEV=0",
    "ptt_device": "/dev/hidraw0",
    "ptt_gpio": 3,
    "txdelay_ms": 300,
    "txtail_ms": 20,
    "tx_level": 0.05,
    "busy_detect": true
  }
}
```

| Setting | What it does |
|---|---|
| `capture_device`, `playback_device` | the sound card, as above |
| `sample_rate` | 48000 unless your card cannot do it; any multiple of 9600 from 19200 works |
| `ptt_device`, `ptt_gpio` | the hidraw node and the GPIO pin wired to PTT (3 on most interfaces) |
| `txdelay_ms` | silence sent after keying, before the data, while the radio switches to transmit |
| `txtail_ms` | silence sent after the data, before unkeying |
| `tx_level` | transmit level, a fraction of full scale (0.5 if not set) |
| `busy_detect` | treat audio energy on the channel as busy, so the node waits for others; off if not set |
| `rx_record` | write all received audio to this WAV file, for checking levels (about 70 MB an hour) |

The rest of the file is the node's own settings, as in `example.json`. The bench files use short contention timings that suit two radios on a bench, not a real HF net.

## Run it

```
./s5066d radio-ft450.json
```

You should see the SIS port and a line describing the radio. Then each burst is one line:

```
radio: tx wn6 short 25 bytes
radio: rx wn6 short 512 bytes, SNR 18.4 dB
```

Point an application, or `s5066-test`, at the SIS port (`127.0.0.1:5066` in the bench files). Stop with Ctrl-C or `systemctl stop`; `s5066d` unkeys the radio as it stops. If a transmission runs more than 3 s past its planned length (a sound card that hangs), `s5066d` unkeys the radio and exits. Only a power cut or `kill -9` can leave the radio keyed, so on air also set the radio's own transmit time-out timer if it has one; starting `s5066d` again unkeys it. If the sound card or interface goes away, it says so and exits with status 1, so a service manager can restart it.

## Setting levels

- **Transmit:** start low. The FT-450's data input wants only 60 mVp-p, so its bench file starts at `tx_level` 0.05, and you may still need a resistive divider in the interface. Raise it while watching ALC: a few dots at most. Too much drive splatters and the far end decodes worse, not better.
- **Receive:** set `rx_record`, let it hear a few bursts, and look at the file in Audacity: bursts well clear of the noise and nowhere near full scale. Remove `rx_record` afterwards.
- **TXDELAY:** neither the FT-450 nor the FT-818 manual gives its switching time, so the bench files use a safe 300 ms. If you can, measure from the PTT edge to RF out and shorten it.
- **Passband:** both bench radios pass only about 400 to 2600 Hz on transmit, narrower than the 3 kHz waveforms, so expect the radios' filters, not the modem, to limit the faster waveforms.

## Identifying your station in CW (optional)

If your licence or your network asks you to identify, `s5066d` can send a short Morse ID that anyone can read by ear. It is off unless you add it, and you choose the text. Add a `station` part to `host`:

```
"host": {
  "station": {
    "cw_id": { "text": "DE M0XYZ", "wpm": 20, "every_minutes": 10 }
  },
  ...
}
```

| Setting | What it does |
|---|---|
| `text` | what to send: letters, digits, spaces and `/ ? . , = - +` |
| `wpm` | speed in words a minute (20 if not set) |
| `tone_hz` | the tone (1800 Hz if not set, the middle of the data signal) |
| `level` | how loud, like `tx_level` (the same as `tx_level` if not set) |
| `at_start` | send it before the first transmission after a quiet spell of 10 minutes or more (on if not set) |
| `every_minutes` | send it again with the next transmission once this many minutes have passed (off if not set) |

The ID only ever goes out at the start of a transmission the node was already allowed to make, straight after `txdelay_ms` and just before the data, so it never starts over another station. An idle node sends nothing. It makes each transmission that carries it a few seconds longer, so keep it short: `s5066d` refuses one longer than 20 s. Keep it well under `dts.max_transmission_ms` too (6 s in the bench files), or the far station may give up waiting for your answer and repeat itself; if you need a longer ID, raise `dts.ack_wait_ms` on the other stations to allow for it. Each ID is one log line, `radio: cw id "DE M0XYZ" (5.5 s) before this transmission`.

Other stations' modems do not hear CW as data, so they may start talking during your ID unless they have `busy_detect` on. Keep `busy_detect` on where you can.

## Station status page (optional)

Add `"status_listen": "127.0.0.1:8066"` to `host`, then open `http://127.0.0.1:8066/` on the Pi. It shows the links, each station heard with its waveform and SNR, what is waiting to be sent, the last bursts, the PTT and the CW ID schedule, and refreshes every 2 seconds. `http://127.0.0.1:8066/status.json` gives the same as JSON for scripts.

It has no password, so keep it on `127.0.0.1`. From another machine, use an SSH tunnel: `ssh -L 8066:127.0.0.1:8066 pi@yourpi`, then open `http://127.0.0.1:8066/` there.

## CAT control (optional)

`s5066d` can set the radio up for you when it starts: the frequency, the radio's data mode on upper sideband (Hamlib's `PKTUSB`: DATA on the FT-450, DIG with USER-U on the FT-818) and the widest receive filter, 3 kHz where the radio has one. It can also key the radio over CAT instead of the CM108's GPIO pin. It does this through Hamlib's `rigctld`, which you run yourself for your radio, so any radio Hamlib knows will do.

Install rigctld from your distribution's Hamlib package (`sudo apt install libhamlib-utils` on Debian, Raspberry Pi OS and Ubuntu), find your radio's model number with `rigctl -l`, and start it, for example for an FT-450 on a USB serial cable:

```
rigctld -m 1027 -r /dev/ttyUSB0 -s 4800
```

Hamlib's numbers for the bench radios are 1027 for the FT-450, 1046 for the FT-450D and 1041 for the FT-818 (from `rigctl -l`, Hamlib 4.6.5). The speed must match the radio's CAT rate menu, 4800 by default on all three. On the FT-450, set menu D TYPE to USER-U once by hand, so its DATA mode takes audio.

Then add a `cat` part to `radio`:

```
"radio": {
  ...
  "cat": { "rigctld": "127.0.0.1:4532", "frequency_hz": 7052300 },
  "ptt": "cat"
}
```

| Setting | What it does |
|---|---|
| `cat.frequency_hz` | the dial frequency to set, in Hz |
| `cat.rigctld` | where rigctld listens (`127.0.0.1:4532`, its own default, if not set) |
| `ptt` | `"cat"` to key the radio through rigctld; `"cm108"`, the GPIO pin, if not set. With `"cat"`, leave out `ptt_device` and `ptt_gpio` |

At start you get one line saying what the radio really set, for example:

```
radio: cat: rigctld 127.0.0.1:4532: 7052300 Hz, PKTUSB, passband 2400 Hz (asked for 3000; the radio set this)
```

Not every radio has a 3 kHz filter in its data mode: Hamlib lists 2400 Hz as the FT-450's widest there, and the FT-818's filter is fixed, so it reports none. That is fine; the line just tells you.

If rigctld is not running, or stops, `s5066d` says so once and keeps trying every 5 s, and sets the radio up again when it comes back. It never stops the node for it. With `"ptt": "cat"` it will not transmit while rigctld is gone: each burst it would have sent is one `burst not sent` line instead. The safety rules of the GPIO pin hold for CAT too: the radio is unkeyed at start, after every burst, if a transmission overruns and when `s5066d` stops. If rigctld is unreachable at that moment the unkey cannot get through, and the log says so, so on air set the radio's own transmit time-out timer as well. rigctld does not unkey the radio when it stops, so if you run it as a service, stop `s5066d` first (or give the `s5066d` unit `After=` your rigctld unit, so systemd stops it first). CAT keying takes a moment longer than the pin, which `txdelay_ms` already covers, as it starts counting only once rigctld has answered.

The package lists Hamlib as a suggestion, so `apt` does not install it unless you ask.

## Running it as a service

A minimal unit, `/etc/systemd/system/s5066d.service`:

```
[Unit]
Description=STANAG 5066 node
After=sound.target

[Service]
User=pi
ExecStart=/home/pi/s5066d-arm64/s5066d /home/pi/radio-ft450.json
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

Then `sudo systemctl enable --now s5066d` and `journalctl -u s5066d -f` to watch it.

## When something is wrong

- `cannot open plughw:...: No such device`: the card name is wrong; check `arecord -l`.
- `Device or resource busy`: another program (often PulseAudio or PipeWire) holds the card. Stop it or give it a different card.
- `no permission to open /dev/hidraw0`: the udev rule is missing or the user is not in `audio`.
- It transmits but the far end hears nothing: check the playback level, `tx_level` and the radio's data mode (USER-U on the FT-450, DIG with USER-U on the FT-818).
- `radio: cat: no rigctld at ...`: rigctld is not running or listens elsewhere; check `rigctl -m 2 -r 127.0.0.1:4532 f` answers with the frequency.
- It never answers: check the receive level, and that Auto Gain Control is off.
