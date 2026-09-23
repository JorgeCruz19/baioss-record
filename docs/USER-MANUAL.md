# User manual — Baioss Record

> A practical guide to operating Baioss Record. It is written in plain language, for the operator's day-to-day work. You do not need any technical knowledge to use the program.
>
> *(This is the English version of `MANUAL-USUARIO.md`; the two are kept in step.)*

---

## 1. What is Baioss Record?

Baioss Record is a **professional multi-channel video recorder**. Each channel is like an independent recorder: you connect a video source to it (an SDI capture card, a USB camera, an NDI signal…) and you can record it, watch it live and schedule automatic recordings.

It is designed to run **24 hours a day, 7 days a week** unattended: if a source loses signal, if the recording process fails or if the disk fills up, the program reacts on its own and warns you.

**Key ideas:**
- Each channel (A, B, C, D…) is independent. What happens to one does not affect the others.
- You always get a **live preview** of every channel, whether it is recording or not.
- You can record **by hand** (Record button) or **on a schedule** (at a fixed time, once or repeatedly).
- The program **looks after itself**: it watches the signal, the disk and the health of every recording.

---

## 1.1. First run: installing FFmpeg (once only)

Baioss Record uses **FFmpeg** as its recording engine. Because of that component's licensing it does not come inside
the installer: it has to be downloaded and left in its folder. It takes a moment and **you only do it once**.

1. Download an FFmpeg build for **64-bit Windows** (the official page, <https://ffmpeg.org/download.html>, links to
   the maintained Windows builds). If you are going to capture with **Blackmagic DeckLink** cards, make sure the
   build includes "decklink" support: not all of them do.
2. Unzip the file and look inside (usually in a `bin` folder) for **`ffmpeg.exe`** and **`ffprobe.exe`**.
3. Copy those **two** files into the `tools\ffmpeg\` folder of the installation (by default
   `C:\Baioss\Record\tools\ffmpeg\`). You will also find a `FFMPEG-README.txt` file there with these same steps.
4. Open Baioss Record.

**How do I know if it is missing?** When you open the program a warning says so and shows the exact folder. While it
is missing the program runs in **demonstration mode**: you can see everything and configure it, but **it does not record**.

## 1.2. Application language

Baioss Record speaks **Spanish and English**. The first time it simply follows the **Windows** language (if your
Windows is in any variant of Spanish it starts in Spanish; otherwise, in English).

You can change it whenever you like in **🛠 Settings → LANGUAGE**. The change is applied **instantly**, without
closing the program or interrupting any recording, and it is remembered for the next runs.

> The log files (`logs\`) are always kept in Spanish: they are meant for technical support.

---

## 1.3. A note about NDI®

Baioss Record can record **NDI®** sources. NDI® is a registered trademark of Vizrt NDI AB; you can find more
information and its tools at <https://ndi.video/>. Baioss Record is not a product of Vizrt NDI AB.

---

## 2. The main screen

When you open the program you see a row with **one panel per channel**. At the very top is the **title bar** with the name "BAIOSS RECORD" and, on the right, the buttons that open the various settings windows.

### 2.1. A channel panel

Each panel shows you, from top to bottom:

- **Header:** the channel letter (A, B…), its name, and the **format of the incoming signal** (for example "1920×1080"). On the right:
  - A **signal status** label:
    - 🟢 **Green ("SIGNAL OK"):** all good, the signal is stable.
    - 🟠 **Amber ("UNSTABLE"):** the signal is arriving with problems.
    - ⚪ **Grey ("NO SIGNAL"):** there is no signal.
  - A red **"● REC"** badge that **flashes while the channel is recording**.

- **Four metric boxes:**
  - **FPS OUT:** how many frames per second are being recorded.
  - **BITRATE:** the "amount of data" in the video (more bitrate means more quality and a bigger file).
  - **DROPPED:** lost frames. If this climbs, the machine is short of power or disk throughput.
  - **DISK / STATUS:** while recording, the **time left** of disk space; when idle, the channel status (Idle, Recording…).

- **The preview (the monitor):** the live picture of the channel. Over it you will see:
  - A **pulsing red frame** when it is recording (like a studio tally).
  - The name of the **active input** (the connected source) at the bottom left.
  - The **timecode** (recording time counter), large, at the bottom right.
  - If something goes wrong, a **warning** across the top (see *Alarms* further down).

- **Audio strip:** the **sound meters** (left "L" and right "R"), with their level in dBFS. If the sound clips, a red **"CLIP"** warning appears.
  With an 8- or 16-channel input (DeckLink or NDI) a **mini meter per pair** ("1-2", "3-4"…) also appears: the pair being
  recorded carries a red dot and a bold label; the others are only metered, so you can see at a glance what each SDI pair
  carries. The large L/R meters show the first recorded pair, and the header of the strip says how many tracks will go
  into the file ("All 16 channels · PCM · 8 tracks").

- **Recording buttons (transport):**
  - **● Record:** starts recording by hand.
  - **■ Stop:** stops the manual recording (it only appears while you are recording by hand).
  - **⏏ Stop scheduled recording:** appears only if a **scheduled** recording is running; it lets you skip *that* recording without affecting the following ones.
  - On the right, the **PRESET** tag tells you **which format will be recorded** when you press Record (for example "ProRes 422 · 1080p25"). Hovering over it shows the technical summary (codec · rate · size · container). It is changed in **⚙ Recording presets** (see section 4).

- **Schedule box:** shows the scheduled recording that is **running** right now (in green, "RUNNING"), or "No recording in progress". The **🕒 Show schedule** button opens the full list for the day.

### 2.2. Recording and stopping by hand

1. Press **● Record** on the channel you want. It starts recording immediately and the monitor frame turns red.
2. When you are done, press **■ Stop**.
3. On stopping, the program **asks what name to save the recording under**. Type a name (or keep the one it suggests) and confirm. If the name already exists it appends a number so that nothing is overwritten.

> If you leave the name empty, it is saved as `Channel_date_time` (for example `A_20260721_203055.mp4`).

> **The web panel works the same way:** when you press **Stop**, the confirmation prompt includes a **File name** field
> with the same suggested name. Type yours and press Enter or **Stop recording**; leave it empty to keep the automatic
> name. **Scheduled** recordings are not asked: they are already saved as `date_Title`. The name given to each recording
> is logged in **📋 Activity** ("Recording saved with a name"), whether it was typed in the application or in the web panel.

### 2.3. The storage indicator (the 💾 pill)

At the top, next to the program name, there is a **pill with the disk space**: for example `💾 250 GB · 78%`. It changes colour according to the health of the disk:
- **Normal:** there is plenty of space.
- 🟠 **Amber (warning):** the disk is starting to fill up.
- 🔴 **Red (critical/emergency):** very little space is left. A very visible **red band** also appears across the top.

This pill is **always** visible, even when you are not recording, so that one glance tells you how the disk is doing.

---

## 3. Inputs — the 🎛 Inputs button

This is where you decide **which video source is connected to each channel**.

**Input types the program supports:**
- **DeckLink (SDI):** professional Blackmagic capture cards. This is the typical broadcast input.
- **USB camera / capture device (DirectShow):** webcams, HDMI-to-USB capture devices, etc.
- **NDI:** video over the network (for example the NDI output of OBS or another NDI source on your network).
- **Demonstration clip:** a sample video that ships with the program (handy for testing).

**How to assign an input to a channel:**
1. Press **🔍 Detect devices** so that the program looks for the connected cards and cameras.
2. On the channel's row, choose:
   - **Video input:** the source (the card or camera).
   - **Audio (DirectShow):** the microphone/sound input, if your source needs one.
   - **Mode / format (DeckLink):** the resolution and rate of the SDI signal (for example "1080i 59.94"). With
     **Automatic** the card itself detects the signal when the input opens: the channel panel shows the detected format
     ("1920×1080 · 59.94i") and, if the card detects nothing, it shows **NO SIGNAL** (no signal or cable, or a
     card/connector that does not autodetect) instead of a black picture with no explanation. Press **📡 Detect signal**
     (with the card free, no channel using it) to see what signal the card detects: the matching mode is selected and,
     on **Apply**, fixed; the safest choice where autodetection fails or is slow. If another program (or another
     channel) holds the card, it says so; and the channel shows **NO SIGNAL** while another process holds it, in any mode.
   - **Card audio (DeckLink):** how many embedded audio channels to request from the card. **Automatic** asks for as
     many as the card allows (16, falling back to 8 or 2). If you know what your signal carries, you can fix 2, 8 or 16.
   - **Pair to record (DeckLink and NDI):** which pair of SDI channels goes into the recording (pair 1-2, 3-4, 5-6…) or
     **All pairs** (to keep each pair in its own track or preserve all the audio; see section 4). For
     example, if the programme is on 1-2 and the international sound on 3-4, pick the pair you want recorded. With NDI
     the program cannot know how many channels the source carries until it connects: all eight possible pairs are
     offered and, if the chosen one does not exist, pair 1-2 is recorded.
   - **🎧 Measure audio:** if you do not know what the signal carries, press this button while the card is free (no
     channel using it): the program listens for a few seconds, shows the level of each pair ("1-2: −8 dB · 3-4:
     silence…") and proposes the first pair with sound. The proposal is not saved until you press **Apply**.
3. Press **Apply**. The channel reconnects to the new source live (it releases the previous one and opens the new one).

> **Note:** a channel's input cannot be changed **while it is recording**. Stop the recording first.

> **About embedded audio:** the card cannot tell how many channels the signal carries; you ask for a number and it
> delivers that number (missing channels arrive as silence). That is why the choice is made here, when configuring the
> input, and does not change while recording: a pair that is quiet at the start is not an absent pair, and a file's
> tracks cannot change halfway through. Under the channel's monitor you can see what is being recorded, for example
> "Pair 3-4 of 8 · PCM".

---

## 4. Recording formats — the ⚙ Recording presets button

A **preset** defines **how** the video is recorded: the file format, the resolution, the quality and the sound. Instead of setting technical things one by one, you pick a ready-made preset.

**The window has three columns:**
- **Formats (left):** categories to filter by (MP4, MKV, ProRes, etc.).
- **Presets (centre):** the list of presets. The built-in ones carry the "factory" tag. You can mark your favourites with the **★ star**.
- **Detail (right):** a summary of the selected preset and the option to apply it.

**How to give a channel a format:**
1. Find and select the preset you want (there is a 🔍 search box at the top).
2. At the bottom right, under **"Apply to:"**, choose the channel (A, B…).
3. Press **Apply to channel**. The **PRESET** tag on the channel panel changes at once to the preset's name, and the
   change is saved: it is kept even if you close the program without having recorded.

**You can also:**
- **＋ New / ✎ Edit / ⧉ Duplicate / 🗑 Delete:** create your own presets from the existing ones.
- **⭱ Import / ⭳ Export:** take your presets to another machine or keep them as a backup.

**Audio tracks (only matters with 8- or 16-channel inputs):** in the preset editor, the **Audio tracks** field decides
how the pairs chosen on the input (see section 3) are stored:
- **Single:** one track with the selection (the pair in stereo, or 5.1/7.1 with the first channels). This is the usual behaviour.
- **PairsAsTracks:** one stereo track per chosen pair, each with its name ("Channels 3-4"), so the programme, the
  international sound or each language stay in separate tracks without mixing.
- **Multichannel:** all chosen channels in a single **PCM** track (MXF, MKV, AVI, WAV; up to 16 fit). With a lossy
  codec (AAC in MP4/MOV/TS, Opus, MP2, MP3) the program stores one stereo track per pair instead, because a lossy
  5.1/7.1 track treats channel 4 as the bass channel (LFE) and strips it.

**PairsAsTracks** and **Multichannel** need **All pairs** on the input. There is a separate guide with typical setups
and how to read the per-pair meters: `MULTICHANNEL-AUDIO-MANUAL.md`.

> The preset only changes **the quality and the file format**. The folder where recordings are saved is configured separately (see section 6).

---

## 5. The schedule — the 🕒 Schedule button

This is what makes the program **record on its own at the time you tell it**, once or repeatedly.

**To create a scheduled recording**, fill in the form at the bottom:
- **Channel:** which channel will record.
- **Repeat:** "Once", "Every day", "Days of the week"…
- **Date:** only if it is "Once".
- **Start time** and **end time** (hours : minutes : seconds). The duration is worked out for you.
- **Days** (weekly only): tick M, T, W, T, F, S, S.
- **Split every … minutes** (optional): breaks the recording into pieces. Each piece is a complete file on its own, so **if one is damaged you do not lose the whole recording**. Strongly recommended for long recordings.
- **Title:** the name the file will carry.

Press **＋ Schedule** to save it.

**The schedule list** (below) shows each task with its times and its **next run**. On each one you can:
- **Edit** its details.
- **Pause / Resume** (suspend it without deleting it).
- **Delete**.

**At the top** there is a **"TODAY"** section with the day's recordings and their status (scheduled / running / recorded).

**Other options (top right):**
- **⬆ Import / ⬇ Export:** save the whole schedule to a file (CSV for Excel or JSON as a backup) and load it back.
- **🔄 Refresh:** refresh the list.

> Scheduled recordings are saved with the name `date_Title` (for example `21-07-2026_News.mp4`).

> **From the web panel too:** the panel's **Schedule** section does the same from a browser: **New task** (or **Add** on a
> channel), edit (pencil), pause/resume (switch) and delete (bin), with the same rules as this window (unique title, no
> overlaps on the same channel, end time different from start time…). The date and the times are chosen with pickers, in
> 24-hour format, and are always **this computer's**, even if the browser is in another time zone. In **Channels**, each
> card shows only the automatic task that is **recording right now** (title, times and what is left); pressing **Stop** on
> it skips just this occurrence and the task stays active for the next ones. Whatever is created, changed or deleted —from
> the web or from here— is logged in **📋 Activity** as "Schedule changed", with the name of who did it.

---

## 6. Destination folder and test pattern — the 🛠 Settings button

Here you configure two things **per channel** (and, for the whole program, the language and the web panel access: 6.3):

### 6.1. Destination folder

This is the **folder where that channel's recordings are saved**.
- Press **Browse…** and pick the folder.
- The path you choose **is kept for good**, until you change it. Even if you close and reopen the program, each channel keeps recording to its own folder.
- If you do not set one, the default folder is used (`recordings`, inside the program's folder).

> **Tip:** it is worth having a different folder per channel, or at least being clear about it, because the file name carries the channel letter at the front to tell them apart.

### 6.2. Test pattern on signal loss

This is a checkbox: **"Test pattern on signal loss (keeps recording bars)"**.
- If it is **ticked** and the source loses signal in the middle of a recording, the program **keeps recording** a bars/warning screen instead of cutting. That way the file is not interrupted and there is a record that the signal was lost.
- If it is **unticked**, losing the signal simply means no new picture reaches the recording.

### 6.3. Web panel and API (opening the web panel from another computer)

The **web panel** (channels, their preview, record/stop, history, activity and storage from a browser) talks to the
program through its **API**. Out of the box the API only answers **on this same computer** (port **5005**) and there is
nothing to configure. This section is for when you want to open the panel **from another computer** or change the port:

| Setting | What it does |
|---|---|
| **Allow connections from other computers on the network** | Off: this computer only (the safe default). On: the API listens on the network. |
| **Port** | 5005 by default. Change it if another program already uses it. |
| **Websites allowed to connect** | The address the web panel is opened from in the browser, for example `http://192.168.1.50:5173` (several, separated by commas). `*` = any. Without it the browser blocks the panel even if the network works. Not needed when the panel uses "Este servidor" (this server). |

Press **Save** and **restart Baioss Record**: these settings apply at start-up. Below, the program tells you **what to
type in the web panel**: this computer's IP and the port. In the web panel, bottom left, click the **"Record · …"** line
→ **Another computer** → type that IP and port → **Test connection** → **Save**. It is remembered in that browser and
applies at once, with nothing to reinstall or rebuild. The panel is in English or Spanish: it follows the browser's
language the first time, and the ES/EN switch at the bottom of its sidebar changes it at once.

> ⚠ **The API has no password.** With "Allow connections…" on, anyone on your network who can reach that port can see
> the channels and **start or stop recordings**. Enable it only on a trusted network (never on a computer exposed to the
> Internet). The first time, Windows may ask for firewall permission: allow it for private networks.

> **If the saved address stops being valid** (for example a fixed IP the computer no longer has, or a port already in
> use), the program **still starts**, in safe mode ("this computer only"), and says so in this same section and in the
> log: a wrong network setting never prevents recording.

---

## 7. Storage — the 🗄 Storage button

This is where you control **what happens to the disk space** in the long run. All changes apply **without restarting**.

> **Safe by default:** the program **deletes nothing** unless YOU enable retention or auto-cleanup.

### 7.1. Automatic retention

This is what **deletes (or archives) old recordings** so that you do not run out of space.
- **Enable automatic retention:** the checkbox that turns all of this on.
- **Keep for (days):** deletes anything older than X days. `0` = do not delete by age.
- **Keep at least free (GB):** if the disk drops below that many free GB, it deletes the oldest ones until space is recovered. `0` = off.
- **Keep at least free (%):** the same as above, but as a percentage. `0` = off.
- **Check every (minutes):** how often it checks (minimum 5).
- **Archive to another folder instead of deleting:** instead of deleting, it moves the old recordings to another folder (press **Browse…** to choose it).

### 7.2. Space alerts and emergency mode

- **Warning (% used):** from that percentage on, the disk pill turns **amber**.
- **Critical (% used):** it turns **red**.
- **Emergency (% used):** the most serious level (red + warning band).
- **Auto-clean on entering emergency:** if switched on, when the disk reaches emergency the program **automatically deletes the oldest recordings that are NOT protected**.
- **Block starting new recordings during the emergency:** stops new recordings from starting while the disk is in emergency (the ones already running carry on).

> The thresholds sort themselves: warning ≤ critical ≤ emergency. Setting `0` disables that threshold.

Remember to press **Save**.

---

## 8. Recording history — the 🗂 Recordings button

This is the list of **everything you have recorded**. It is for looking things up, protecting them and locating files.

**At the top** you can filter by **date range** and by **channel**, and you get a summary (how many recordings and how much space they take).

**Each row** shows: channel, **file name**, date, time, duration, size, operator and the **protection** status.

**Buttons on each row:**
- **🔒 Protect:** marks the recording as **protected** (green chip). Automatic cleanup will never delete it.
- **★ Important:** marks it as important (amber chip). Also excluded from cleanup.
- **○ Normal:** removes the protection (it goes back into automatic cleanup).
- **📂:** opens the folder where the file is.

> **Use "Protect" or "Important"** on the recordings you never want to lose. Automatic cleanup (section 7) always respects those marks.

---

## 9. The activity log — the 📋 Activity button

This is the **machine's memory**: what was recorded, who recorded it, how, why each recording ended… and also
what **never** got recorded. It is there to answer questions after the fact, like *"why did last night's
recording cut out at 3?"* or *"the 20:00 schedule left no file, what happened?"*.

**Each row tells you** the date and time, a **level** (grey for routine, amber for warnings, red for errors),
the **event** in plain words, the channel, the operator and the detail. For example:

| Event | Detail |
|---|---|
| Recording started | Manual · Jorge |
| Recording started | Scheduled «News 20:00» · 21-07-2026_News |
| Recording ended | Stopped, the disk was running out · 1 h 12 min · 3 files · 22.4 GB |
| Could not start | «News» · The destination folder is not writable |
| Schedule skipped | «News» · The channel was already recording |
| Recording interrupted | The recording process was forcibly terminated · carried on in a new piece |
| Damaged file | 21-07-2026_News_9.mp4 · 2.7 GB · no index: cannot be played as is |

The last two go together: if the recording process dies halfway through a file, the program **keeps recording
in a new file** (numbered next in sequence) and tells you that the cut file cannot be played as it is. That
file is not empty — the video is inside — but it is missing its index; ask your supplier about recovering it.
It is also what the **"Unverified recording"** alarm on the channel panel means.

**The filters at the top** narrow things down by period, channel, type (*Everything*, *Recordings only*,
*Problems only*) and level. To review a whole night, the practical one is **Problems only**: it leaves just
what went wrong.

**⬇ Export** saves *what you are looking at* to a CSV file, with the filters applied. It opens in Excel and is
what you would hand to a client or keep on file.

> It is **read only**: nothing is deleted or changed here. Entries are kept for **30 days** and the oldest ones
> retire on their own; that has nothing to do with your recordings, which are managed in 🗄 Storage.

---

## 10. Useful concepts (a plain glossary)

- **Channel:** an independent recorder (A, B, C…). Each one has its own source, format and folder.
- **Input / source:** where the video comes from (SDI card, camera, NDI…).
- **Preset:** the "recipe" of quality and format a recording is made with.
- **Signal (LOCK):** that a picture is arriving steadily. Green = fine; amber = with problems; grey = no signal.
- **Test pattern (bars):** the warning screen that gets recorded when the signal is lost (if you enabled that option).
- **Split:** breaking a long recording into several files, for safety.
- **Timecode:** the time counter of the recording in progress.
- **24/7 recording:** continuous operation, with monitoring and automatic recovery.
- **Protection (Protected / Important / Normal):** the mark that decides whether a recording can be deleted by automatic cleanup or not.

### Alarms you may see on the monitor
The program warns you with a banner over the preview when it detects:
- **Black picture:** the picture has gone black.
- **Frozen picture:** the picture is not changing (it has "stuck").
- **Audio silence:** there is no sound.
- **Test pattern:** the warning screen is being recorded because the signal was lost.
- **Disk:** there is little space left.
- **Disk not responding:** the destination disk has stopped accepting writes. The recording **waits without cutting** and resumes on its own when the disk comes back; if it lasts more than a few seconds, check that disk (cable, USB, antivirus).

---

## 11. Tips and frequently asked questions

**How do I start recording a channel from scratch?**
1. **🎛 Inputs** → assign the source to the channel and press Apply.
2. **⚙ Recording presets** → choose the format/quality and apply it to the channel.
3. **🛠 Settings** → choose the channel's destination folder.
4. On the main screen, press **● Record** (or schedule the recording in **🕒 Schedule**).

**Is the destination folder lost when I restart?**
No. Once you choose it, it **is kept** until you change it.

**Does the program delete my recordings on its own?**
Only if you enable **retention** or **auto-cleanup** in 🗄 Storage. By default it deletes nothing. And it **never** deletes anything you have marked as **Protected** or **Important**.

**What happens if the signal drops in the middle of a recording?**
The file **is not lost**. If you enabled the test pattern, it keeps recording a warning screen; in any case the program watches and recovers on its own when the signal comes back.

**Can I record several channels at once?**
Yes, that is the whole point. Each channel records independently.

**Why can't I change a channel's input?**
Because it is recording. Stop the recording and you will be able to reassign it.

---

*Baioss Record — multi-channel broadcast recording. This manual describes the operational use of the program.*
