@file:OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)

package com.pcremote.center.ui

import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.isImeVisible
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Dashboard
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Terminal
import androidx.compose.material.icons.filled.Bolt
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Snackbar
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.pcremote.center.PcRemoteApp
import com.pcremote.center.ui.screens.ConnectScreen
import com.pcremote.center.ui.screens.DashboardScreen
import com.pcremote.center.ui.screens.DetailScreen
import com.pcremote.center.ui.screens.SettingsScreen
import com.pcremote.center.ui.screens.ShortcutsScreen
import com.pcremote.center.ui.screens.TerminalScreen
import com.pcremote.center.ui.theme.Pc

private data class Tab(val route: String, val label: String, val icon: ImageVector)

private val Tabs = listOf(
    Tab("dashboard", "Dashboard", Icons.Filled.Dashboard),
    Tab("terminal", "Terminal", Icons.Filled.Terminal),
    Tab("shortcuts", "Shortcuts", Icons.Filled.Bolt),
    Tab("settings", "Settings", Icons.Filled.Settings),
)

@Composable
fun AppRoot() {
    val container = (LocalContext.current.applicationContext as PcRemoteApp).container
    val nav = rememberNavController()
    val hasPcs by remember { container.store.pcs }.collectAsState()
    val start = remember { if (container.store.pcs.value.isEmpty()) "connect" else "dashboard" }
    val backEntry by nav.currentBackStackEntryAsState()
    val route = backEntry?.destination?.route
    val showBar = route in Tabs.map { it.route } && !WindowInsets.isImeVisible

    val snackbar = remember { SnackbarHostState() }
    var lastIsError by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        container.connection.messages.collect { m ->
            lastIsError = m.isError
            snackbar.showSnackbar(m.text, duration = if (m.text.length > 70 || m.isError) SnackbarDuration.Long else SnackbarDuration.Short)
        }
    }

    Box(Modifier.fillMaxSize().background(Pc.Charcoal).imePadding()) {
        Scaffold(
            containerColor = Pc.Charcoal,
            contentWindowInsets = WindowInsets(0),
            snackbarHost = {
                SnackbarHost(snackbar) { data ->
                    Snackbar(
                        data,
                        containerColor = if (lastIsError) Color(0xFF3A1518) else Pc.CardHigh,
                        contentColor = Pc.TextPrimary,
                    )
                }
            },
            bottomBar = {
                if (showBar) {
                    NavigationBar(containerColor = Pc.Panel) {
                        for (t in Tabs) {
                            NavigationBarItem(
                                selected = route == t.route,
                                onClick = {
                                    nav.navigate(t.route) {
                                        popUpTo(nav.graph.findStartDestination().id) { saveState = true }
                                        launchSingleTop = true
                                        restoreState = true
                                    }
                                },
                                icon = { Icon(t.icon, t.label) },
                                label = { Text(t.label) },
                                colors = NavigationBarItemDefaults.colors(
                                    selectedIconColor = Pc.Cyan, selectedTextColor = Pc.Cyan,
                                    indicatorColor = Pc.Cyan.copy(alpha = 0.14f),
                                    unselectedIconColor = Pc.TextDim, unselectedTextColor = Pc.TextDim,
                                ),
                            )
                        }
                    }
                }
            },
        ) { inner ->
            NavHost(
                nav, startDestination = start,
                modifier = Modifier.padding(bottom = inner.calculateBottomPadding()),
                enterTransition = { fadeIn(tween(220)) },
                exitTransition = { fadeOut(tween(160)) },
                popEnterTransition = { fadeIn(tween(220)) },
                popExitTransition = { fadeOut(tween(160)) },
            ) {
                composable("connect") {
                    ConnectScreen(
                        onBack = if (hasPcs.isNotEmpty()) ({ if (!nav.popBackStack()) nav.navigate("dashboard") }) else null,
                        onPaired = {
                            nav.navigate("dashboard") { popUpTo(0) { inclusive = true } }
                        },
                    )
                }
                composable("dashboard") {
                    DashboardScreen(
                        onOpenDetail = { nav.navigate("detail/$it") },
                        onOpenSettings = { nav.navigate("settings") { launchSingleTop = true } },
                        onAddPc = { nav.navigate("connect") },
                    )
                }
                composable("detail/{kind}") { e ->
                    DetailScreen(e.arguments?.getString("kind") ?: "cpu", onBack = { nav.popBackStack() })
                }
                composable("terminal") { TerminalScreen() }
                composable("shortcuts") { ShortcutsScreen() }
                composable("settings") { SettingsScreen(onAddPc = { nav.navigate("connect") }) }
            }
        }
    }
}
