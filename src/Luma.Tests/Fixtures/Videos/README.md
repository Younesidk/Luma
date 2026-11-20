Synthetic test videos generated with FFmpeg's `testsrc2` / `sine` sources (no third-party content):

    ffmpeg -f lavfi -i testsrc2=size=160x90:rate=30 -t 1 -c:v libx264 -pix_fmt yuv420p -movflags +faststart h264.mp4
    ... same source with libx264 (.mov), libvpx-vp9 (.webm), libx265 -tag:v hvc1 (hevc.mp4), plus an AAC sine tone (with-audio.mp4)

`corrupt.mp4` is `h264.mp4` with 12 kB overwritten by random bytes at offset 1500; `not-a-video.mp4` is plain text.
