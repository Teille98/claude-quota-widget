// Claude Quota Android app + home-screen widget. AGP 9: built-in Kotlin support.
plugins {
    id("com.android.application")
}

android {
    namespace = "dev.teo.claudequota"
    compileSdk = 36
    defaultConfig {
        applicationId = "dev.teo.claudequota"
        minSdk = 31 // Android 12: responsive widgets, system corner radius, RemoteViews tints
        targetSdk = 36
        versionCode = 100
        versionName = "1.0.0"
    }
    // Release key lives outside the repo: path and passwords in ~/.gradle/gradle.properties (CLAUDEQUOTA_*).
    // Without them, assembleRelease produces an unsigned APK.
    val store = providers.gradleProperty("CLAUDEQUOTA_STORE_FILE").orNull
    signingConfigs {
        if (store != null) create("release") {
            storeFile = file(store)
            storePassword = providers.gradleProperty("CLAUDEQUOTA_STORE_PASSWORD").get()
            keyAlias = providers.gradleProperty("CLAUDEQUOTA_KEY_ALIAS").get()
            keyPassword = providers.gradleProperty("CLAUDEQUOTA_KEY_PASSWORD").get()
        }
    }
    buildTypes {
        // The development build installs side by side with the signed release.
        debug {
            applicationIdSuffix = ".debug"
            versionNameSuffix = "-dev"
        }
        release {
            isMinifyEnabled = false
            if (store != null) signingConfig = signingConfigs.getByName("release")
        }
    }
    // Usage script shared with the Windows app.
    sourceSets.getByName("main").assets.srcDir("../../shared")
}

dependencies {
    implementation("androidx.work:work-runtime-ktx:2.12.0")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303") // real implementation (android.jar only has stubs)
}
