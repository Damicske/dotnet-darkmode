﻿' Copyright (C) 2020 Ryan Bester

Imports System.Runtime.InteropServices
Imports System.Windows.Forms

' Library for adding support for Windows 10 dark mode.
' Ported from https://github.com/ysc3839/win32-darkmode to be compatible with .NET.
Public Module DarkMode

#Region "Fields"
    ''' <summary>
    ''' The build number of the current Windows version.
    ''' </summary>
    Private _buildNumber As UInt32
    ''' <summary>
    ''' Whether the dark mode functions have been loaded from the DLL.
    ''' </summary>
    Private _initialised As Boolean = False

    ''' <summary>
    ''' Whether the current Windows version supports dark mode.
    ''' </summary>
    Private _darkModeSupported As Boolean = False
    ''' <summary>
    ''' Whether dark mode has been enabled by the user. Only applicable if the SYSTEM theme is used.
    ''' </summary>
    Private _darkModeEnabled As Boolean = False

    ' Function pointers:
    Private _fnOpenNcThemeData As TypeOpenNcThemeData = Nothing ' Ordinal 49
    Private _fnRefreshImmersiveColorPolicyState As TypeRefreshImmersiveColorPolicyState = Nothing ' Ordinal 104
    Private _fnGetIsImmersiveColorUsingHighContrast As TypeGetIsImmersiveColorUsingHighContrast = Nothing ' Ordinal 106
    Private _fnShouldAppsUseDarkMode As TypeShouldAppsUseDarkMode = Nothing ' Ordinal 132
    Private _fnAllowDarkModeForWindow As TypeAllowDarkModeForWindow = Nothing ' Ordinal 133
    Private _fnAllowDarkModeForApp As TypeAllowDarkModeForApp = Nothing ' Ordinal 135
    Private _fnSetPreferredAppMode As TypeSetPreferredAppMode = Nothing ' Ordinal 135
    Private _fnIsDarkModeAllowedForWindow As TypeIsDarkModeAllowedForWindow = Nothing ' Ordinal 137
#End Region

#Region "Constants"
    ' Window messages
    Private Const WM_CREATE = &H1
    Private Const WM_SETTINGCHANGED = &H1A
    Private Const WM_THEMECHANGED = &H31A
    Private Const WM_NCACTIVATE = &H86

    Private Const WCA_USEDARKMODECOLORS = 26
#End Region

#Region "Enumerations"
    Public Enum Theme
        SYSTEM
        DARK
        LIGHT
    End Enum

    Private Enum PreferredAppMode
        AppModeDefault
        AllowDark
        ForceDark
        ForceLight
        Max
    End Enum

    Private Enum ImmersiveHCCacheMode
        IHCM_USE_CACHED_VALUE
        IHCM_REFRESH
    End Enum
#End Region

#Region "Structures"
    <StructLayout(LayoutKind.Sequential)>
    Private Structure WinCompositionAttrData
        Public Attribute As UInt32
        Public Data As IntPtr
        Public SizeOfData As UInt64
    End Structure
#End Region

#Region "DLL Imports"
    <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function LoadLibrary(ByVal lpLibFileName As String) As IntPtr
    End Function

    <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function FreeLibrary(ByVal hLibModule As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function GetProcAddress(ByVal hModule As IntPtr, lpProcName As UInt32) As IntPtr
    End Function

    <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function CallWindowProc(ByVal hModule As IntPtr, lpProcName As String) As IntPtr
    End Function

    <DllImport("ntdll.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Sub RtlGetNtVersionNumbers(ByRef major As UInt32, ByRef minor As UInt32, ByRef build As UInt32)
    End Sub

    <DllImport("user32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function SetWindowCompositionAttribute(ByVal hWnd As IntPtr, ByRef data As WinCompositionAttrData) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    <DllImport("user32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function SetProp(ByVal hWnd As IntPtr, ByVal lpString As String, ByVal hData As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    ' Assumes wParam is on 32 bit. Different on 16 bit and 64 bit.
    <DllImport("user32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function SendMessage(ByVal hWnd As IntPtr, ByVal msg As UInt32, ByVal wParam As Int32, ByVal lParam As Int32) As Int32
    End Function

    <DllImport("uxtheme.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function SetWindowTheme(ByVal hWnd As IntPtr, pszSubAppName As String, pszSubIdList As String) As Int32
    End Function

    Private Delegate Function TypeOpenNcThemeData(ByVal hWnd As IntPtr, ByVal pszClassList As String) As IntPtr ' Ordinal 49
    Private Delegate Sub TypeRefreshImmersiveColorPolicyState() ' Ordinal 104
    Private Delegate Function TypeGetIsImmersiveColorUsingHighContrast(mode As ImmersiveHCCacheMode) As <MarshalAs(UnmanagedType.Bool)> Boolean ' Ordinal 106
    Private Delegate Function TypeShouldAppsUseDarkMode() As <MarshalAs(UnmanagedType.Bool)> Boolean ' Ordinal 132
    Private Delegate Function TypeAllowDarkModeForWindow(ByVal hWnd As IntPtr, allow As Boolean) As <MarshalAs(UnmanagedType.Bool)> Boolean ' Ordinal 133
    Private Delegate Function TypeAllowDarkModeForApp(<MarshalAs(UnmanagedType.Bool)> allow As Boolean) As <MarshalAs(UnmanagedType.Bool)> Boolean ' Ordinal 135
    Private Delegate Function TypeSetPreferredAppMode(ByVal appMode As PreferredAppMode) As PreferredAppMode ' Ordinal 135
    Private Delegate Function TypeIsDarkModeAllowedForWindow(ByVal hWnd As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean ' Ordinal 137
#End Region

#Region "Methods"
    ''' <summary>
    ''' Convert two WORDs into a DWORD.
    ''' </summary>
    ''' <param name="loword">The low order WORD.</param>
    ''' <param name="hiword">The high order WORD.</param>
    ''' <returns>The DWORD.</returns>
    Private Function MakeDWORD(loword As UInt16, hiword As UInt16) As UInt32
        Return (loword << 16) Or hiword
    End Function

    ''' <summary>
    ''' Checks if the current Windows version supports dark mode.
    ''' </summary>
    ''' <param name="buildNumber">The Windows build number.</param>
    ''' <returns>True if the Windows version supports dark mode, or false if it doesn't.</returns>
    Private Function CheckBuildNumber(buildNumber As UInt32) As Boolean
        Return _
            buildNumber = 17763 Or ' Version 1809
            buildNumber = 18362 Or ' Version 1903
            buildNumber = 18363 Or ' Version 1909
            buildNumber = 19041 Or ' Version 2004
            buildNumber = 19042    ' Version 2010
    End Function

    ''' <summary>
    ''' Refreshes the colour of the titlebar to match the theme.
    ''' </summary>
    ''' <param name="hWnd">The handle to the window.</param>
    ''' <param name="theme">The theme for the window.</param>
    Private Sub RefreshTitleBarThemeColour(hWnd As IntPtr, theme As Theme)
        Dim dark As Integer = 0

        If theme = Theme.SYSTEM _
            Or theme = Theme.DARK Then

            If theme = Theme.SYSTEM Then
                If _fnIsDarkModeAllowedForWindow IsNot Nothing Then
                    If _fnIsDarkModeAllowedForWindow(hWnd) Then
                        If _fnShouldAppsUseDarkMode IsNot Nothing Then
                            dark = IIf(_fnShouldAppsUseDarkMode(), 1, 0)
                        End If

                    End If
                End If
            Else
                dark = 1
            End If
        End If

        If (_buildNumber < 18362) Then
            SetProp(hWnd, "UseImmersiveDarkModeColors", dark)
        Else
            Dim size = Marshal.SizeOf(Of Integer)
            Dim ptr As IntPtr = Marshal.AllocHGlobal(size)
            Marshal.WriteInt32(ptr, dark)
            SetWindowCompositionAttribute(hWnd, New WinCompositionAttrData() With {.Attribute = WCA_USEDARKMODECOLORS, .Data = ptr, .SizeOfData = size})
            Marshal.FreeHGlobal(ptr)
        End If
    End Sub

    Private Function IsColourSchemeChangeMessage(lParam As IntPtr) As Boolean
        Dim flag As Boolean = False

        If lParam <> 0 And Marshal.PtrToStringUni(lParam) = "ImmersiveColorSet" Then
            _fnRefreshImmersiveColorPolicyState()
            flag = True
        End If
        _fnGetIsImmersiveColorUsingHighContrast(ImmersiveHCCacheMode.IHCM_REFRESH)

        Return flag
    End Function

    ''' <summary>
    ''' Loads all the functions needed for dark mode.
    ''' </summary>
    ''' <returns>True if successful, False if a critical error occurs. Note that unsupported Windows versions will still return True as it is not a critical error.</returns>
    Private Function InitDarkMode() As Boolean
        If Not _initialised Then
            _initialised = True

            Dim major, minor, build As UInt32
            RtlGetNtVersionNumbers(major, minor, build)
            _buildNumber = build And Not &HF0000000

            If major = 10 And minor = 0 And CheckBuildNumber(_buildNumber) Then
                ' Load uxtheme.dll. We cannot use DllImport here since the symbol is version specific.
                Dim libPtr As IntPtr = LoadLibrary("uxtheme.dll")
                If libPtr = 0 Then
                    ' Error loading library
                    Return False
                End If

                ' Get the address of the symbols
                Dim pa49 As IntPtr = GetProcAddress(libPtr, MakeDWORD(49, 0)) ' Ordinal 49
                If pa49 <> 0 Then
                    ' Store pointer to function 
                    _fnOpenNcThemeData = Marshal.GetDelegateForFunctionPointer(Of TypeOpenNcThemeData)(pa49)
                End If

                Dim pa104 As IntPtr = GetProcAddress(libPtr, MakeDWORD(104, 0)) ' Ordinal 104
                If pa104 <> 0 Then
                    _fnRefreshImmersiveColorPolicyState = Marshal.GetDelegateForFunctionPointer(Of TypeRefreshImmersiveColorPolicyState)(pa104)
                End If

                Dim pa106 As IntPtr = GetProcAddress(libPtr, MakeDWORD(106, 0)) ' Ordinal 106
                If pa106 <> 0 Then
                    _fnGetIsImmersiveColorUsingHighContrast = Marshal.GetDelegateForFunctionPointer(Of TypeGetIsImmersiveColorUsingHighContrast)(pa106)
                End If

                Dim pa132 As IntPtr = GetProcAddress(libPtr, MakeDWORD(132, 0)) ' Ordinal 132
                If pa132 <> 0 Then
                    _fnShouldAppsUseDarkMode = Marshal.GetDelegateForFunctionPointer(Of TypeShouldAppsUseDarkMode)(pa132)
                End If

                Dim pa133 As IntPtr = GetProcAddress(libPtr, MakeDWORD(133, 0)) ' Ordinal 133
                If pa133 <> 0 Then
                    _fnAllowDarkModeForWindow = Marshal.GetDelegateForFunctionPointer(Of TypeAllowDarkModeForWindow)(pa133)
                End If

                Dim pa135 As IntPtr = GetProcAddress(libPtr, MakeDWORD(135, 0)) ' Ordinal 135
                If pa135 <> 0 Then
                    ' < 1903
                    If build < 18362 Then
                        _fnAllowDarkModeForApp = Marshal.GetDelegateForFunctionPointer(Of TypeAllowDarkModeForApp)(pa135)
                    Else
                        _fnSetPreferredAppMode = Marshal.GetDelegateForFunctionPointer(Of TypeSetPreferredAppMode)(pa135)
                    End If
                End If

                Dim pa137 As IntPtr = GetProcAddress(libPtr, MakeDWORD(137, 0)) ' Ordinal 137
                If pa137 <> 0 Then
                    _fnIsDarkModeAllowedForWindow = Marshal.GetDelegateForFunctionPointer(Of TypeIsDarkModeAllowedForWindow)(pa137)
                End If

                FreeLibrary(libPtr)

                If _fnOpenNcThemeData IsNot Nothing _
                    And _fnRefreshImmersiveColorPolicyState IsNot Nothing _
                    And _fnShouldAppsUseDarkMode IsNot Nothing _
                    And _fnAllowDarkModeForWindow IsNot Nothing _
                    And (_fnAllowDarkModeForApp IsNot Nothing Or _fnSetPreferredAppMode IsNot Nothing) _
                    And _fnIsDarkModeAllowedForWindow IsNot Nothing Then

                    _darkModeSupported = True
                    _darkModeEnabled = _fnShouldAppsUseDarkMode() And Not SystemInformation.HighContrast
                End If
            Else
                ' Unsupported Windows version
                Return True
            End If
        End If

        Return True
    End Function

    Public Sub SetAppTheme(theme As Theme)
        If Not InitDarkMode() Then
            Return
        End If

        If _darkModeEnabled Then
            ' <1903
            If _fnAllowDarkModeForApp IsNot Nothing Then
                If theme = Theme.SYSTEM Or theme = Theme.DARK Then
                    _fnAllowDarkModeForApp(True)
                Else
                    _fnAllowDarkModeForApp(False)
                End If
            ElseIf _fnSetPreferredAppMode IsNot Nothing Then
                Select Case theme
                    Case Theme.SYSTEM
                        _fnSetPreferredAppMode(PreferredAppMode.AllowDark)
                    Case Theme.DARK
                        _fnSetPreferredAppMode(PreferredAppMode.ForceDark)
                    Case Theme.LIGHT
                        _fnSetPreferredAppMode(PreferredAppMode.ForceLight)
                End Select
            End If

            If _fnRefreshImmersiveColorPolicyState IsNot Nothing Then
                _fnRefreshImmersiveColorPolicyState()
            End If
        End If
        'TODO: FixDarkScrollbar()
    End Sub

    Public Sub WndProc(window As Form, m As Message, theme As Theme)
        If Not InitDarkMode() Then
            Return
        End If

        Select Case m.Msg
            Case WM_CREATE
                If _fnAllowDarkModeForWindow IsNot Nothing Then
                    _fnAllowDarkModeForWindow(m.HWnd, 1)
                End If

                RefreshTitleBarThemeColour(m.HWnd, theme)
            Case WM_SETTINGCHANGED
                If IsColourSchemeChangeMessage(m.LParam) Then
                    _darkModeEnabled = _fnShouldAppsUseDarkMode() And Not SystemInformation.HighContrast

                    RefreshTitleBarThemeColour(window.Handle, theme)
                End If
        End Select
    End Sub

    Public Sub UpdateWindowTheme(window As Form, theme As Theme)
        RefreshTitleBarThemeColour(window.Handle, theme)

        ' Make window inactive and active again to refresh title bar colour.
        SendMessage(window.Handle, WM_NCACTIVATE, 0, 0)
        SendMessage(window.Handle, WM_NCACTIVATE, 1, 0)
    End Sub
#End Region

End Module