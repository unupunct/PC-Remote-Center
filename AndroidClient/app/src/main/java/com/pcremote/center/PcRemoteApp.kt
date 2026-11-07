package com.pcremote.center

import android.app.Application
import com.pcremote.center.core.AppContainer

class PcRemoteApp : Application() {
    lateinit var container: AppContainer
        private set

    override fun onCreate() {
        super.onCreate()
        container = AppContainer(this)
    }
}
