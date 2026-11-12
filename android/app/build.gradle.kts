import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val appVersion = System.getenv("THUMBFREE_VERSION") ?: "0.1.0"
// 0.1.0 → 100, 1.2.3 → 10203
val appVersionCode = appVersion.substringBefore('-').split('.').map { it.toInt() }.let { (a, b, c) -> a * 10000 + b * 100 + c }

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
    }
}

dependencies {
    implementation(project(":core"))
    implementation(project(":whisper"))
    implementation(project(":whisper-v82"))

    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
}
