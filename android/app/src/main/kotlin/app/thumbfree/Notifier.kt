package app.thumbfree

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.widget.Toast

/**
 * ThumbFree's notifications. None are about marketing: they show while the bubble is listening, when the microphone
 * goes silent mid-dictation ("don't speak into the void"), and when a dictation couldn't be typed (tap to copy it).
 */
class Notifier(private val context: Context) {
    private val manager = context.getSystemService(NotificationManager::class.java)

    init {
        manager.createNotificationChannel(
            NotificationChannel(LISTENING, "Listening", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Shown while the bubble is recording"
                setShowBadge(false)
            },
        )
        manager.createNotificationChannel(
            NotificationChannel(ALERTS, "Dictation problems", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "When the microphone goes silent or a dictation couldn't be typed"
            },
        )
    }

    val allowed: Boolean
        get() = (Build.VERSION.SDK_INT < 33 || context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) &&
            manager.areNotificationsEnabled()

    /** For the microphone service while the bubble records. */
    fun listening(): Notification = builder(LISTENING)
        .setContentTitle("ThumbFree is listening")
        .setContentText("On this phone, offline. Tap the bubble to finish.")
        .setOngoing(true)
        .setCategory(Notification.CATEGORY_SERVICE)
        .build()

    fun micSilent() = post(
        SILENT_ID,
        builder(ALERTS)
            .setContentTitle("ThumbFree can't hear you")
            .setContentText("The microphone went silent. Another app or a call may be using it.")
            .setCategory(Notification.CATEGORY_ERROR)
            .setTimeoutAfter(60_000)
            .build(),
    )

    fun clearMicSilent() = manager.cancel(SILENT_ID)

    fun interrupted() = post(
        SILENT_ID,
        builder(ALERTS)
            .setContentTitle("Dictation interrupted")
            .setContentText("Another app took the microphone. What you said so far is being written.")
            .setCategory(Notification.CATEGORY_ERROR)
            .setTimeoutAfter(60_000)
            .build(),
    )

    /** A dictation that couldn't be typed: kept in the notification until copied or dismissed. */
    fun undelivered(text: String, reason: String) {
        val id = UNDELIVERED_BASE + (nextId++ % 50)
        val copy = PendingIntent.getBroadcast(
            context, id,
            Intent(context, CopyReceiver::class.java).putExtra(EXTRA_TEXT, text).putExtra(EXTRA_ID, id),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val public = builder(ALERTS).setContentTitle("Dictation saved").setContentText("Unlock to see it.").build()
        post(
            id,
            builder(ALERTS)
                .setContentTitle("$reason Tap to copy.")
                .setContentText(text)
                .setStyle(Notification.BigTextStyle().bigText(text))
                .setContentIntent(copy)
                .setAutoCancel(true)
                .setVisibility(Notification.VISIBILITY_PRIVATE)
                .setPublicVersion(public)
                .build(),
        )
    }

    private fun builder(channel: String) = Notification.Builder(context, channel)
        .setSmallIcon(R.drawable.ic_mic)
        .setColor(context.getColor(R.color.tf_accent))
        .setOnlyAlertOnce(true)

    private fun post(id: Int, notification: Notification) {
        if (allowed) manager.notify(id, notification)
    }

    companion object {
        const val LISTENING = "listening"
        const val ALERTS = "alerts"
        const val LISTENING_ID = 1
        private const val SILENT_ID = 2
        private const val UNDELIVERED_BASE = 100
        const val EXTRA_TEXT = "text"
        const val EXTRA_ID = "id"
        private var nextId = 0
    }
}

/** "Tap to copy" on a saved dictation. */
class CopyReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val text = intent.getStringExtra(Notifier.EXTRA_TEXT) ?: return
        context.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("Dictation", text))
        context.getSystemService(android.app.NotificationManager::class.java).cancel(intent.getIntExtra(Notifier.EXTRA_ID, 0))
        // Android 13+ shows its own "copied" confirmation.
        if (Build.VERSION.SDK_INT < 33) Toast.makeText(context, "Dictation copied", Toast.LENGTH_SHORT).show()
    }
}
