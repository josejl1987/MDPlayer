# Native SPC backend runtime payload

`libmdplayer_spc.so` (built by `native/MDPlayer.SpcNative` via cmake) is copied
here automatically by the native build's post-build step and by the
`MDPlayer.Fmp.Cli` project when present. It is a generated artifact and is not
committed. The managed wrapper (`SpcNativeSession`) locates it via
`AppContext.BaseDirectory/runtimes/linux-x64/native/libmdplayer_spc.so`, with
an `MDPLAYER_SPC_NATIVE` environment-variable override.
