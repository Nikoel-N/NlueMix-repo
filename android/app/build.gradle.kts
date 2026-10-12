plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.nluemix.sender"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.nluemix.sender"
        minSdk = 29          // Android 10 起才有系统音频采集
        targetSdk = 34
        // 与电脑端版本号保持一致（电脑端在 src/NlueMix/NlueMix.csproj 的 <Version>）
        versionCode = 2
        versionName = "0.2.0"
    }

    // ★ 对照实验开关：同一个 APK，只改 android:allowAudioPlaybackCapture。
    //   构建命令：gradle assembleDebug -PallowCapture=true / -PallowCapture=false
    //   用来严格验证"这个属性是不是 MediaProjection 能否采集的唯一变量"。
    defaultConfig {
        manifestPlaceholders["allowCapture"] = (project.findProperty("allowCapture") ?: "false").toString()
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            // 用调试签名，方便直接装
            signingConfig = signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }

    // ★ 用户版 / 开发者版
    //
    //   构建命令：
    //     用户版    gradle assembleRelease
    //     开发者版  gradle assembleRelease -PdevBuild=true
    //
    //   差别只在界面：开发者版多出「（调试）读取当前屏幕内容」这类排查入口。
    //   采集、协议、传输完全一致 —— 两个版本的音频行为必须一样，
    //   否则"用户版出问题、开发者版正常"这种事会让排查失去意义。
    buildFeatures { buildConfig = true }
    defaultConfig {
        buildConfigField("boolean", "DEV_BUILD",
            (project.findProperty("devBuild") ?: "false").toString())
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.12.0")
    implementation("androidx.appcompat:appcompat:1.6.1")
    implementation("com.google.android.material:material:1.11.0")
}
