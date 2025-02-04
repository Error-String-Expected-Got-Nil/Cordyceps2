# Cordyceps2
*A TAS mod for Rain World*

*By Error: String Expected, Got Nil*

Cordyceps2 is a simple TASing tool that allows you to manually control the tickrate of the game, or pause it entirely, 
during gameplay.

"Tickrate" in this case refers to the number of *physics ticks* per second, which is distinct from graphical framerate.
Rain World's default tickrate is 40 ticks per second, though this may change even in normal gameplay; for instance, due 
to eating a mushroom, being near an Echo, or being in the Depths. Cordyceps2 allows you to artifically cap this tickrate
to any value from 1 to 40, or even stop it entirely and step through the game tick-by-tick. This is primarily for use in
testing precise movement techniques. It also adjusts the speedrun timer to account for its artifical slowdown, making it
count as if the game had been run normally.

Cordyceps2 *also* has a specialized recording feature. A video encoder has been built directly into the mod, synced to
the game's tickrate, allowing it to capture video of your slow-motion gameplay as if it were real-time, allowing you to
play it back and see what things looked like.

## Controls
*All rebindable in the mod's settings menu.*

- Show/hide info panel: \[M\]
- Toggle tickrate cap: \[Comma\]
- Increase tickrate cap: \[Equals\]
- Decrease tickrate cap: \[Minus\]
- Toggle pause: \[Period\]
- Tick advance: \[Forward Slash\]
- Reset tick counter: \[Semicolon\]
- Pause/unpause tick counter: \[Single Quote\]
- Start Recording \[R\]
- Stop Recording \[T\]

### Note on Tick Advance
Tick advance only works when tick pause is active. Pressing it causes the tick pause to be disabled, and then 
automatically re-enable once the next tick passes. To make an input register on the tick you advance to, simply hold 
down any inputs you want to make while you press the tick advance button.

## Recording
The time control features all work on any platform, but support for the recording feature has only been made for 
Windows. Besides this, however, it should *just work.* The default output directory for recorded files is 
`C:\cordyceps2`, with the file named `Cordyceps2` followed by a timestamp for when recording was started.

The default configuration is very conservative and likely much less demanding than your computer can handle. I highly 
suggest trying out some more strenuous settings and seeing how the game performs. So long as there isn't any noticable
lag, you should be completely fine to use them.

### Recording Settings
The following are some more detailed notes and technical explanations for the various settings available for configuring
the video encoder. Not necessary to just use Cordyceps2, but may be of interest.

- **Output Resolution:** Resolution to scale the output video to. "Native" means the resolution the game is actually 
running at. The only other option is "1920x1080", mostly to make uploading to YouTube at decent quality easier. The 
encoder cannot create quality from nothing; native resolution is the best the video can *actually* be, setting this to 
1920x1080 simply upscales it. This is rather performance-intensive, and it's probably a better idea to simply record at 
native resolution and upscale afterward, as it will look the same anyway.

- **Recording FPS:** Framerate to record at. There is no practical benefit to recording at any more than the default of 
40 FPS, since recording is synced to the game's tickrate, and 40 Hz is the highest normal tickrate.

- **Fragment Video:** Ordinarily, the metadata and headers for data in the video are written all at once, in a single 
place. Fragmenting the video instead places them throughout the video file, next to where it's used. If recording is 
stopped unexecpectedly (for instance, from the game crashing), this might prevent the video from being corrupted and 
unrecoverable. However, fragmented video isn't as universally supported as non-fragmented video (Discord appears to 
support it fine, if that's a concern). Cordyceps2 will output a fragment on each keyframe.

- **Output Directory:** Directory to put recorded videos into. Already explained earlier in this section, and by the 
description box below this option in the actual settings menu.

- **Keyframe Interval:** Modern video formats utilize a technique where, rather than saving the entirety of every frame,
they mostly save the differences between frames, with only a few "keyframes" that contain a full image. This value is
the number of regular frames for every keyframe. Smaller values are easier to seek around in a video player, but produce
larger video files.

- **Constant Rate Factor:** A factor which determines the compression level of the video. Higher numbers are more 
compressed. It may be a good idea to leave this low and re-compress the video after recording if it is too large.

- **Encoder Preset:** Preset settings for the encoder to use. "Slower" presets are more efficient but less performant,
"faster" presets are the opposite. Feel free to increase this if your computer seems to be handling recording fine on a
faster preset; you may be able to get a better video with a slower one.

### Extra Settings
These are mostly test/debug options I added for my own use, and didn't see a reason to remove. You can safely ignore
these, but they are documented here regardless.

- **Log Level:** Logging level for low-level FFmpeg interop function calls. Default only shows errors, but you can 
increase it if you'd like to peek at some of the informational logging put out by the encoder as it works.
 FFmpeg logging is forwarded to the standard `consoleLog.txt`.

- **Video Buffer Pool Depth:** This is an implementation detail relating to the custom-built encoder used by Cordyceps2.
Without getting too technical, this is essentially the maximum number of video frames which can be queued for encoding 
at any given time before new frames will be dropped, with 0 meaning no limit. It is not recommended to un-limit this, as
it could lead to extreme memory usage if too many frames get queued at once, and this memory usage will persist until 
recording ends.

- **Do Profiling:** If enabled, some extra profiling information will be captured while the encoder runs, and printed to
the log when recording ends. The only output of this that should need explanation is "Relative video encode rate", which
is the ratio of record time to time spent doing video encoding. So long as this number is greater than 1.00x, the 
encoder is able to keep up with the amount of incoming video data. Useful as a measure for how much more strenuous you 
can make the encoder settings.

- **Bit Exact Scaling:** Adds the flags SWS_ACCURATE_RND and SWS_BITEXACT to the encoder's libswscale frame scaler.
I'm not sure that this actually has any effect for Cordyceps2's use-case; I only added this to test it myself, and
just decided it was easier to move it here than take it out again. Has a notable performance cost.