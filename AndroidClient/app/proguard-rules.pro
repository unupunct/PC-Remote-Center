# kotlinx.serialization: keep generated serializers and @Serializable classes of this app.
-keepattributes *Annotation*, InnerClasses, Signature, EnclosingMethod
-dontnote kotlinx.serialization.**
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keepclasseswithmembers class kotlinx.serialization.json.** { kotlinx.serialization.KSerializer serializer(...); }
-keep,includedescriptorclasses class com.pcremote.center.**$$serializer { *; }
-keepclassmembers class com.pcremote.center.** {
    *** Companion;
}
-keepclasseswithmembers class com.pcremote.center.** {
    kotlinx.serialization.KSerializer serializer(...);
}

# OkHttp optional TLS providers
-dontwarn okhttp3.internal.platform.**
-dontwarn org.conscrypt.**
-dontwarn org.bouncycastle.**
-dontwarn org.openjsse.**
