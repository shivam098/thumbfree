# JNI methods are looked up by name from native code.
-keep class app.thumbfree.whisper.WhisperNative {
    native <methods>;
}
