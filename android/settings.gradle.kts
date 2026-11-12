pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
    plugins {
        id("com.android.application") version "8.7.3"
        id("com.android.library") version "8.7.3"
        id("org.jetbrains.kotlin.android") version "2.0.21"
        id("org.jetbrains.kotlin.jvm") version "2.0.21"
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "thumbfree"

// The dictation logic is plain Kotlin and builds anywhere. The Android modules need the Android SDK (and NDK for
// whisper.cpp); they're skipped when there's no SDK, or when THUMBFREE_CORE_ONLY=1, so `gradlew :core:test` works on
// any machine with a JDK.
include(":core")

val localSdk = file("local.properties").takeIf { it.exists() }?.let { f ->
    java.util.Properties().apply { f.inputStream().use { load(it) } }.getProperty("sdk.dir")
}
val sdk = System.getenv("ANDROID_HOME") ?: System.getenv("ANDROID_SDK_ROOT") ?: localSdk
if (System.getenv("THUMBFREE_CORE_ONLY") != "1" && sdk != null) {
    include(":whisper", ":whisper-v82", ":app")
}
