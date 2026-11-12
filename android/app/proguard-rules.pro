# ThumbFree keeps no reflection-based libraries. The JNI bridges need their classes as they are: whisper's rules are
# in :whisper; sherpa-onnx's native code reads its config classes' fields by name.
-keepattributes SourceFile,LineNumberTable
-keep class com.k2fsa.sherpa.onnx.** { *; }
