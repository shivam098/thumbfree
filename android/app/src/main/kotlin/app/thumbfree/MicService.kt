package app.thumbfree

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder

/**
 * Lets the bubble use the microphone. Android lets an app that isn't on screen record only from a foreground service
 * of type "microphone", so this runs (with its "listening" notification) exactly while the bubble records.
 */
class MicService : Service() {
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val ok = try {
            val notification = thumbFree.notifier.listening()
            if (Build.VERSION.SDK_INT >= 29) {
                startForeground(Notifier.LISTENING_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
            } else {
                startForeground(Notifier.LISTENING_ID, notification)
            }
            true
        } catch (e: RuntimeException) { // ForegroundServiceStartNotAllowedException, SecurityException
            false
        }
        BubbleService.instance?.onMicReady(ok)
        if (!ok) stopSelf()
        return START_NOT_STICKY
    }

    companion object {
        /** False if Android refused to start it (the caller is told through [BubbleService.onMicReady] otherwise). */
        fun start(context: Context): Boolean = try {
            context.startForegroundService(Intent(context, MicService::class.java))
            true
        } catch (e: RuntimeException) {
            false
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, MicService::class.java))
        }
    }
}
