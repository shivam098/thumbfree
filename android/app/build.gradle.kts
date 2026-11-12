import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val appVersion = System.getenv("THUMBFREE_VERSION") ?: "0.1.0"
// 0.1.0 → 100, 1.2.3 → 10203
val appVersionCode = appVersion.substringBefore('-').split('.').map { it.toInt() }.let { (a, b, c) -> a * 10000 + b * 100 + c }

// sherpa-onnx (Parakeet speech engine) with ONNX Runtime linked in: one self-contained .so per ABI, 16 KB aligned.
// Downloaded once and pinned by SHA-256 rather than kept in git (38 MB).
val sherpaVersion = "2.0.1"
val sherpaSha256 = "07e042d97f3a6bd5b7fe74d4eef0513dc01a714b16c15a903250095aba43a575"
val sherpaAar = file("libs/sherpa-onnx-static-link-onnxruntime-$sherpaVersion.aar")

fun sha256(f: File): String = java.security.MessageDigest.getInstance("SHA-256").let { md ->
    f.inputStream().use { input ->
        val buffer = ByteArray(1 shl 20)
        while (true) {
            val n = input.read(buffer)
            if (n < 0) break
            md.update(buffer, 0, n)
        }
    }
    md.digest().joinToString("") { "%02x".format(it) }
}

if (!sherpaAar.isFile || sha256(sherpaAar) != sherpaSha256) {
    val url = "https://github.com/k2-fsa/sherpa-onnx/releases/download/v$sherpaVersion/${sherpaAar.name}"
    logger.lifecycle("Downloading $url")
    sherpaAar.parentFile.mkdirs()
    val part = File(sherpaAar.path + ".part")
    java.net.URI(url).toURL().openStream().use { input -> part.outputStream().use { input.copyTo(it) } }
    val actual = sha256(part)
    check(actual == sherpaSha256) { "${sherpaAar.name}: expected SHA-256 $sherpaSha256, got $actual" }
    part.renameTo(sherpaAar) || error("Couldn't save ${sherpaAar.name}")
}

android {
    namespace = "app.thumbfree"
    compileSdk = 35
    System.getenv("ANDROID_NDK_VERSION")?.let { ndkVersion = it }

    defaultConfig {
        applicationId = "app.thumbfree.android"
        minSdk = 26
        targetSdk = 35
        versionCode = appVersionCode
        versionName = appVersion
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        ndk {
            // Phones are arm64; x86_64 is for emulators. Releases pass -Pthumbfree.abis=arm64-v8a.
            abiFilters += (findProperty("thumbfree.abis") as String? ?: "arm64-v8a,x86_64").split(',')
        }
    }

    signingConfigs {
        // Release signing comes from CI secrets; without them a release build is signed with the debug key.
        val keystore = System.getenv("THUMBFREE_KEYSTORE")
        if (keystore != null && file(keystore).exists()) {
            create("release") {
                storeFile = file(keystore)
                storePassword = System.getenv("THUMBFREE_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("THUMBFREE_KEY_ALIAS")
                keyPassword = System.getenv("THUMBFREE_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }

    sourceSets["androidTest"].assets.srcDirs(rootProject.file("../tests/vectors"), rootProject.file("../tests/audio"))

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    packaging {
        jniLibs {
            // Store native code uncompressed and page-aligned: smaller install, loads straight from the APK.
            useLegacyPackaging = false
        }
    }
}

base.archivesName.set("ThumbFree-$appVersion")

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
        // The sherpa-onnx AAR may be built with a newer Kotlin; its API is plain data classes and JNI calls.
        freeCompilerArgs.add("-Xskip-metadata-version-check")
    }
}

dependencies {
    implementation(project(":core"))
    implementation(project(":whisper"))
    implementation(project(":whisper-v82"))
    implementation(files(sherpaAar))

    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
}
