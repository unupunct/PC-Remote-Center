package com.pcremote.center.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.shape.RoundedCornerShape
import com.pcremote.center.core.Level

/** Palette: charcoal background, black panels, cyan/electric-blue accents, purple highlights. */
object Pc {
    val Charcoal = Color(0xFF0E1115)
    val Panel = Color(0xFF080A0D)
    val Card = Color(0xFF14181E)
    val CardHigh = Color(0xFF1B2028)
    val Outline = Color(0xFF262D38)
    val Cyan = Color(0xFF22D3EE)
    val Blue = Color(0xFF3B82F6)
    val Purple = Color(0xFF8B5CF6)
    val Amber = Color(0xFFF5A524)
    val Red = Color(0xFFEF4444)
    val Green = Color(0xFF34D399)
    val TextPrimary = Color(0xFFE8EDF3)
    val TextSecondary = Color(0xFF9AA6B5)
    val TextDim = Color(0xFF647084)

    val AccentBrush: Brush get() = Brush.linearGradient(listOf(Cyan, Blue))
    val PurpleBrush: Brush get() = Brush.linearGradient(listOf(Purple, Blue))

    fun levelColor(level: Level): Color = when (level) {
        Level.Normal -> Cyan
        Level.Warning -> Amber
        Level.Critical -> Red
    }

    /** Second colour of the gauge gradient for the level. */
    fun levelColor2(level: Level): Color = when (level) {
        Level.Normal -> Blue
        Level.Warning -> Color(0xFFFFC857)
        Level.Critical -> Color(0xFFFF7A7A)
    }
}

private val Scheme = darkColorScheme(
    primary = Pc.Cyan,
    onPrimary = Color(0xFF00212A),
    primaryContainer = Color(0xFF0B3A47),
    onPrimaryContainer = Color(0xFFBDF1FB),
    secondary = Pc.Purple,
    onSecondary = Color.White,
    secondaryContainer = Color(0xFF2E2352),
    onSecondaryContainer = Color(0xFFE2D8FF),
    tertiary = Pc.Blue,
    background = Pc.Charcoal,
    onBackground = Pc.TextPrimary,
    surface = Pc.Card,
    onSurface = Pc.TextPrimary,
    surfaceVariant = Pc.CardHigh,
    onSurfaceVariant = Pc.TextSecondary,
    surfaceContainer = Pc.Card,
    surfaceContainerHigh = Pc.CardHigh,
    surfaceContainerHighest = Pc.CardHigh,
    surfaceContainerLow = Pc.Panel,
    outline = Pc.Outline,
    outlineVariant = Pc.Outline,
    error = Pc.Red,
    onError = Color.White,
)

private val AppTypography = Typography(
    headlineSmall = TextStyle(fontSize = 22.sp, fontWeight = FontWeight.SemiBold, letterSpacing = 0.sp),
    titleLarge = TextStyle(fontSize = 20.sp, fontWeight = FontWeight.SemiBold),
    titleMedium = TextStyle(fontSize = 16.sp, fontWeight = FontWeight.SemiBold),
    titleSmall = TextStyle(fontSize = 14.sp, fontWeight = FontWeight.Medium),
    bodyLarge = TextStyle(fontSize = 16.sp),
    bodyMedium = TextStyle(fontSize = 14.sp),
    bodySmall = TextStyle(fontSize = 12.sp),
    labelLarge = TextStyle(fontSize = 14.sp, fontWeight = FontWeight.SemiBold, letterSpacing = 0.3.sp),
    labelMedium = TextStyle(fontSize = 12.sp, fontWeight = FontWeight.Medium, letterSpacing = 0.4.sp),
    labelSmall = TextStyle(fontSize = 10.sp, fontWeight = FontWeight.Medium, letterSpacing = 0.6.sp),
)

val Mono = FontFamily.Monospace

@Composable
fun PcTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = Scheme,
        typography = AppTypography,
        shapes = Shapes(
            small = RoundedCornerShape(10.dp),
            medium = RoundedCornerShape(16.dp),
            large = RoundedCornerShape(22.dp),
        ),
        content = content,
    )
}
