plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
}

android {
    namespace = "com.koutube"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.koutube"
        minSdk = 24                     // Android 7.0
        targetSdk = 35
        versionCode = 8
        versionName = "0.0.8"
    }

    // GitHub's build signs releases with the KouTube key from the repository secrets; a local release build
    // falls back to the debug key, so it installs over a debug build for testing
    signingConfigs {
        System.getenv("KOUTUBE_KEYSTORE")?.let { ks ->
            create("release") {
                storeFile = file(ks)
                storePassword = System.getenv("KOUTUBE_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("KOUTUBE_KEY_ALIAS")
                keyPassword = System.getenv("KOUTUBE_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
            // no R8 for now: the release is the very build that was tested, only signed (shrinking needs its own testing)
            isMinifyEnabled = false
            isShrinkResources = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { compose = true; buildConfig = true }
    packaging { resources.excludes += setOf("/META-INF/{AL2.0,LGPL2.1}") }
}

dependencies {
    testImplementation("junit:junit:4.13.2")

    implementation(platform("androidx.compose:compose-bom:2024.12.01"))
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")

    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")

    val media3 = "1.5.1"
    implementation("androidx.media3:media3-exoplayer:$media3")
    implementation("androidx.media3:media3-ui:$media3")
}
