BAIOSS RECORD — FFMPEG IS STILL MISSING (required step, done only once)
================================================================================

Baioss Record uses FFmpeg as its capture and recording engine. Because of the licensing
of that component we cannot ship it inside the installer: you have to download it and
leave it in THIS folder. It takes a moment and nothing has to be installed.

WHILE IT IS MISSING, the program opens and you can configure it, but it DOES NOT RECORD
(it starts in demonstration mode and warns you when it opens).

(Hay una versión en español de este archivo en FFMPEG-LEEME.txt, en esta misma carpeta.)


WHAT YOU HAVE TO DO
--------------------------------------------------------------------------------

1. Download an FFmpeg build for 64-bit Windows.

   The usual starting point is the official FFmpeg page (https://ffmpeg.org/download.html),
   which links to the Windows builds maintained by third parties.

   IMPORTANT if you are going to capture with Blackmagic DeckLink cards: you need a build
   that includes "decklink" support. Not every build has it. You can check with step 4.

2. Unzip what you downloaded. Inside (usually in a "bin" folder) you will find:

       ffmpeg.exe
       ffprobe.exe

3. Copy THOSE TWO FILES into this very folder, next to this README:

       ...\Baioss\Record\tools\ffmpeg\

   Nothing else is needed: no installation, no system variables to change, no reboot.

4. (Optional, recommended) Check that the build works. Open this folder, type "cmd" in the
   address bar of File Explorer and press Enter; then run:

       ffmpeg.exe -hide_banner -version

   And, if you use DeckLink, also:

       ffmpeg.exe -hide_banner -f decklink -list_devices 1 -i dummy

   If that last one lists your cards, the build is the right one.

5. Open Baioss Record. If the files were copied correctly the warning is gone and you can
   record normally.


COMMON PROBLEMS
--------------------------------------------------------------------------------

· "I copied the files but it still warns me."
  Check that they are in THIS folder (not inside another "bin" subfolder) and that they are
  named exactly ffmpeg.exe and ffprobe.exe. Close the program completely and open it again.

· "Windows will not let me copy here."
  The installation grants write permission over this folder to users. If it still complains,
  copy the files as an administrator (right-click in File Explorer > Run as administrator)
  or ask your supplier for help.

· "ffprobe.exe is missing."
  Both files are needed: ffmpeg.exe does the recording and ffprobe.exe verifies that the
  recordings were closed correctly.


If you have any doubts, please contact your Baioss supplier.
