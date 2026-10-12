plugins {
    id("com.android.application")
}

android {
    namespace = "com.nluemix.tonetest"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.nluemix.tonetest"
        minSdk = 29
        targetSdk = 34
        versionCode = 1
        versionName = "1.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
