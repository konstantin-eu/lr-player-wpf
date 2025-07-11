# SubtitleVideoPlayerWpf
Windows WPF app part of Langrepeater german learning stack https://github.com/konstantin-eu/langrepeater
Video player with subtitles support and following features:
- Each subtitle is repeteated 3 times
- rewind to next/prev subtitle

# build

## Brightness change
from win command line cmd
"C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x86\fxc.exe" /T ps_2_0 /E main /Fo Resources/BrightnessShader.ps BrightnessShader.fx

# Control
Use right/left keys to rewind to next/prev subtitle

# tested with following video format
**Stream 0**
Codec: H264 - MPEG-4 AVC (part 10) (avc1)
Type: Video
Video resolution: 1280x720
Buffer dimensions: 1280x720
Frame rate: 30.000300
Decoded format:
Orientation: Top left
Color primaries: ITU-R BT.709
Color transfer function: ITU-R BT.709
Color space: ITU-R BT.709 Range
Chroma location: Left

**Stream 1**
Codec: MPEG AAC Audio (mp4a)
Type: Audio
Channels: Stereo
Sample rate: 48000 Hz
Bits per sample: 32

# app screenshots and video
TODO

# Note on Code Generation
Parts of this project were programmed with the assistance of a large language model (LLM).
As such, some code may not reflect standard best practices or optimal design choices.
Contributions and improvements are welcome!

# LICENSE
TODO