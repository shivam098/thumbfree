plugins {
    id("com.android.library")
}

// The faster whisper.cpp build for ARMv8.2 phones (dot product + fp16). Native code only: it's the same CMake project
// as :whisper with a different variant, packaged as libthumbfree_whisper_v82.so. :whisper picks it at runtime.
android {
    namespace = "app.thumbfree.whisper.v82"
    compileSdk = 35
    System.getenv("ANDROID_NDK_VERSION")?.let { ndkVersion = it }

    defaultConfig {
        minSdk = 26
        ndk {
            abiFilters += listOf("arm64-v8a")
        }
        externalNativeBuild {
            cmake {
                arguments += listOf("-DTF_VARIANT=v82", "-DANDROID_STL=c++_static", "-DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON")
            }
        }
    }

    externalNativeBuild {
        cmake {
            path = file("../whisper/src/main/cpp/CMakeLists.txt")
        }
    }
}
