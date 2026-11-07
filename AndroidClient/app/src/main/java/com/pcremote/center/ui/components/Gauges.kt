package com.pcremote.center.ui.components

import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.rotate
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.pcremote.center.core.Level
import com.pcremote.center.ui.theme.Pc

/** Animated 270 degree gauge drawn on a Canvas. A null [percent] draws only the empty track ("n/a"). */
@Composable
fun CircularGauge(
    percent: Float?,
    level: Level,
    centerText: String,
    modifier: Modifier = Modifier,
    strokeWidth: Dp = 10.dp,
    centerSize: Int = 22,
    caption: String? = null,
) {
    val target = ((percent ?: 0f) / 100f).coerceIn(0f, 1f)
    val anim by animateFloatAsState(target, tween(800, easing = FastOutSlowInEasing), label = "gauge")
    val c1 = Pc.levelColor(level)
    val c2 = Pc.levelColor2(level)
    Box(modifier.aspectRatio(1f), contentAlignment = Alignment.Center) {
        Canvas(Modifier.fillMaxSize()) {
            val sw = strokeWidth.toPx()
            val inset = sw / 2 + 2.dp.toPx()
            val arcSize = Size(size.width - 2 * inset, size.height - 2 * inset)
            val topLeft = Offset(inset, inset)
            drawArc(Pc.Outline, 135f, 270f, false, topLeft, arcSize, style = Stroke(sw, cap = StrokeCap.Round))
            if (percent != null && anim > 0.002f) {
                rotate(135f) {
                    // soft glow under the arc
                    drawArc(
                        color = c1.copy(alpha = 0.18f), startAngle = 0f, sweepAngle = 270f * anim, useCenter = false,
                        topLeft = topLeft, size = arcSize, style = Stroke(sw * 1.7f, cap = StrokeCap.Round),
                    )
                    drawArc(
                        brush = Brush.sweepGradient(0f to c1, 0.75f to c2, 1f to c2, center = center),
                        startAngle = 0f, sweepAngle = 270f * anim, useCenter = false,
                        topLeft = topLeft, size = arcSize, style = Stroke(sw, cap = StrokeCap.Round),
                    )
                }
            }
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(
                centerText,
                fontSize = centerSize.sp,
                fontWeight = androidx.compose.ui.text.font.FontWeight.Bold,
                color = if (percent == null) Pc.TextDim else Pc.TextPrimary,
                textAlign = TextAlign.Center,
            )
            if (caption != null) Text(caption, style = MaterialTheme.typography.labelSmall, color = Pc.TextDim)
        }
    }
}

/** Horizontal usage bar (per-core bars, disk usage). */
@Composable
fun UsageBar(percent: Float?, level: Level, modifier: Modifier = Modifier, height: Dp = 8.dp) {
    val target = ((percent ?: 0f) / 100f).coerceIn(0f, 1f)
    val anim by animateFloatAsState(target, tween(500), label = "bar")
    Box(
        modifier
            .fillMaxWidth()
            .height(height)
            .background(Pc.Outline, RoundedCornerShape(50))
    ) {
        if (percent != null) {
            Box(
                Modifier
                    .fillMaxHeight()
                    .fillMaxWidth(anim)
                    .background(Brush.horizontalGradient(listOf(Pc.levelColor(level), Pc.levelColor2(level))), RoundedCornerShape(50))
            )
        }
    }
}

/** A series of (timestamp ms, percentage) points. */
data class ChartSeries(val label: String, val color: Color, val points: List<Pair<Long, Float>>)

/**
 * Scrolling time-based line chart on a Canvas. The x axis always spans [windowMs] ending at [endTimeMs], so the chart
 * slides left as new samples arrive. Values are percentages (0-100).
 */
@Composable
fun LineChart(
    series: List<ChartSeries>,
    windowMs: Long,
    endTimeMs: Long,
    modifier: Modifier = Modifier,
    height: Dp = 140.dp,
) {
    val measurer = rememberTextMeasurer()
    val labelStyle = TextStyle(fontSize = 9.sp, color = Pc.TextDim)
    val hasData = series.any { it.points.size >= 2 }
    Box(modifier.fillMaxWidth().height(height)) {
        Canvas(Modifier.fillMaxSize()) {
            val left = 30.dp.toPx()
            val bottom = 16.dp.toPx()
            val top = 4.dp.toPx()
            val w = size.width - left - 4.dp.toPx()
            val h = size.height - bottom - top
            // grid + y labels
            for (i in 0..4) {
                val y = top + h * (1 - i / 4f)
                drawLine(Pc.Outline.copy(alpha = if (i == 0) 0.9f else 0.5f), Offset(left, y), Offset(left + w, y), strokeWidth = 1.dp.toPx())
                val t = measurer.measure("${i * 25}", labelStyle)
                drawText(t, topLeft = Offset(left - t.size.width - 4.dp.toPx(), y - t.size.height / 2f))
            }
            // x labels
            val startLabel = measurer.measure("-${formatWindow(windowMs)}", labelStyle)
            drawText(startLabel, topLeft = Offset(left, size.height - startLabel.size.height))
            val endLabel = measurer.measure("now", labelStyle)
            drawText(endLabel, topLeft = Offset(left + w - endLabel.size.width, size.height - endLabel.size.height))

            val startMs = endTimeMs - windowMs
            for (s in series) {
                val pts = s.points.filter { it.first >= startMs }
                if (pts.size < 2) continue
                val line = Path()
                val fill = Path()
                pts.forEachIndexed { idx, (t, v) ->
                    val x = left + w * ((t - startMs).toFloat() / windowMs).coerceIn(0f, 1f)
                    val y = top + h * (1 - (v / 100f).coerceIn(0f, 1f))
                    if (idx == 0) { line.moveTo(x, y); fill.moveTo(x, top + h); fill.lineTo(x, y) } else { line.lineTo(x, y); fill.lineTo(x, y) }
                }
                val lastX = left + w * ((pts.last().first - startMs).toFloat() / windowMs).coerceIn(0f, 1f)
                fill.lineTo(lastX, top + h)
                fill.close()
                drawPath(fill, Brush.verticalGradient(listOf(s.color.copy(alpha = 0.28f), s.color.copy(alpha = 0.0f)), startY = top, endY = top + h))
                drawPath(line, s.color, style = Stroke(2.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round))
            }
        }
        if (!hasData) {
            Text(
                "Collecting data...",
                modifier = Modifier.align(Alignment.Center),
                style = MaterialTheme.typography.bodySmall,
                color = Pc.TextDim,
            )
        }
    }
}

private fun formatWindow(ms: Long): String {
    val s = ms / 1000
    return if (s % 60 == 0L) "${s / 60}m" else "${s}s"
}

/** Colour legend below a chart. */
@Composable
fun ChartLegend(series: List<ChartSeries>, modifier: Modifier = Modifier) {
    Row(modifier, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
        for (s in series) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.width(10.dp).height(10.dp).background(s.color, RoundedCornerShape(3.dp)))
                Text("  ${s.label}", style = MaterialTheme.typography.labelMedium, color = Pc.TextSecondary)
            }
        }
    }
}
