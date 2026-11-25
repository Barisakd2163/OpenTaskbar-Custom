#define UNICODE
#define _UNICODE
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <limits.h>
#include <wchar.h>
#include <string.h>

#define ABS_AUTOHIDE    0x0000001
#define ABS_ALWAYSONTOP 0x0000002

#define WATCHDOG_PATH_CAPACITY 1024
#define ANALYZER_PATH_CAPACITY 32768
#define ANALYZER_COMMAND_CAPACITY (ANALYZER_PATH_CAPACITY * 2 + 128)
#define HEARTBEAT_TIMEOUT_MS 4000
#define HEARTBEAT_MISSES_REQUIRED 3

static void AttachToDefaultDesktop(void) {
    HDESK hDesk = OpenDesktopW(L"Default", 0, FALSE, GENERIC_ALL);
    if (hDesk) {
        SetThreadDesktop(hDesk);
        CloseDesktop(hDesk);
    }
}

static BOOL CALLBACK EnumSecondaryTrays(HWND hwnd, LPARAM lParam) {
    (void)lParam;
    WCHAR className[64];
    if (GetClassNameW(hwnd, className, 64) > 0) {
        if (wcscmp(className, L"Shell_SecondaryTrayWnd") == 0) {
            ShowWindow(hwnd, SW_SHOW);
            SetWindowPos(hwnd, NULL, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        }
    }
    return TRUE;
}

// Returns Start/Search/Control Center to the positions recorded before the dock anchored them. The journal is
// atomic (written via a temp file) and PID-checked so a recycled HWND can never be moved by a stale entry.
// Returns FALSE when an entry belongs to another process now, so the journal is kept for a later recovery pass.
static BOOL RestoreFlyoutPositions(void) {
    WCHAR localAppData[WATCHDOG_PATH_CAPACITY];
    DWORD envLen = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, WATCHDOG_PATH_CAPACITY);
    if (envLen == 0 || envLen >= WATCHDOG_PATH_CAPACITY) return TRUE;

    WCHAR path[WATCHDOG_PATH_CAPACITY];
    if (_snwprintf_s(path, WATCHDOG_PATH_CAPACITY, _TRUNCATE, L"%s\\OpenTaskbar\\flyout-positions.txt", localAppData) < 0) {
        return TRUE;
    }
    FILE *file = _wfopen(path, L"r");
    if (!file) return TRUE;

    BOOL ownedBySomeoneElse = FALSE;
    unsigned long long handleValue = 0;
    unsigned int processId = 0;
    int left = 0;
    int top = 0;
    while (fwscanf(file, L"%llu %u %d %d", &handleValue, &processId, &left, &top) == 4) {
        HWND handle = (HWND)(UINT_PTR)handleValue;
        if (!IsWindow(handle)) continue;

        DWORD actualProcessId = 0;
        GetWindowThreadProcessId(handle, &actualProcessId);
        if (actualProcessId != (DWORD)processId) {
            ownedBySomeoneElse = TRUE;
            continue;
        }

        SetWindowPos(handle, NULL, left, top, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
    fclose(file);
    if (!ownedBySomeoneElse) DeleteFileW(path);
    return !ownedBySomeoneElse;
}

// A killed tray host leaves app icons registered with its destroyed window; ask apps to re-register them.
static void BroadcastTaskbarCreated(void) {
    UINT message = RegisterWindowMessageW(L"TaskbarCreated");
    if (message) SendNotifyMessageW((HWND)0xFFFF, message, 0, 0);
}

static void RestoreNativeTaskbars(void) {
    AttachToDefaultDesktop();

    // 0. The dock may be gone, but its flyouts are hosted by long-lived shell processes. Put them back first.
    RestoreFlyoutPositions();

    // 1. Read journal if available to recover original auto-hide/always-on-top state. Keep it until the
    //    taskbar is actually visible; deleting it on an Explorer restart would destroy the only recovery clue.
    WCHAR journalPath[WATCHDOG_PATH_CAPACITY] = {0};
    BOOL hasJournal = FALSE;
    WCHAR localAppData[WATCHDOG_PATH_CAPACITY];
    DWORD state = 0;
    DWORD envLen = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, WATCHDOG_PATH_CAPACITY);
    if (envLen > 0 && envLen < WATCHDOG_PATH_CAPACITY
        && _snwprintf_s(journalPath, WATCHDOG_PATH_CAPACITY, _TRUNCATE, L"%s\\OpenTaskbar\\taskbar-state.txt", localAppData) >= 0) {
        FILE *jf = _wfopen(journalPath, L"r");
        if (jf) {
            unsigned int readVal = 0;
            if (fwscanf(jf, L"%u", &readVal) == 1) {
                state = (DWORD)readVal & (ABS_AUTOHIDE | ABS_ALWAYSONTOP);
                hasJournal = TRUE;
            }
            fclose(jf);
        }
    }

    // 2. Locate primary taskbar
    HWND hPrimary = FindWindowW(L"Shell_TrayWnd", NULL);
    if (!hPrimary) return;

    // 3. Re-assert state and visibility with retries (defeats transient Explorer re-hides)
    for (int attempt = 0; attempt < 3; attempt++) {
        APPBARDATA abd;
        ZeroMemory(&abd, sizeof(abd));
        abd.cbSize = sizeof(APPBARDATA);
        abd.hWnd = hPrimary;
        abd.lParam = (LPARAM)state;
        SHAppBarMessage(ABM_SETSTATE, &abd);

        ShowWindow(hPrimary, SW_SHOW);
        SetWindowPos(hPrimary, NULL, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);

        EnumWindows(EnumSecondaryTrays, 0);

        if (attempt < 2) {
            Sleep(60);
        }
    }

    // Explorer can re-hide the tray right after we show it; do not drop the journal unless it stayed visible.
    for (int attempt = 0; attempt < 3 && !IsWindowVisible(hPrimary); attempt++) {
        ShowWindow(hPrimary, SW_SHOW);
        EnumWindows(EnumSecondaryTrays, 0);
        Sleep(60);
    }
    if (!IsWindowVisible(hPrimary)) return;

    if (hasJournal) DeleteFileW(journalPath);
    BroadcastTaskbarCreated();
}

static void DiagnosticWarning(LPCWSTR message) {
    OutputDebugStringW(message);
}

static BOOL IsAbsolutePath(LPCWSTR path) {
    size_t length = wcslen(path);
    if (length < 3 || wcschr(path, L'"') || wcschr(path, L'\r') || wcschr(path, L'\n')) return FALSE;
    return (path[0] == L'\\' && path[1] == L'\\')
        || (((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z'))
            && path[1] == L':' && (path[2] == L'\\' || path[2] == L'/'));
}

static BOOL ParseWatchdogArgument(LPCWSTR data, DWORD *parentPid, WCHAR token[128]) {
    LPCWSTR colon = wcschr(data, L':');
    if (!colon || colon == data || (size_t)(colon - data) > 10) return FALSE;
    for (LPCWSTR digit = data; digit < colon; digit++) {
        if (*digit < L'0' || *digit > L'9') return FALSE;
    }
    WCHAR *end = NULL;
    unsigned long value = wcstoul(data, &end, 10);
    if (end != colon || value == 0 || value > INT_MAX) return FALSE;
    size_t tokenLength = wcslen(colon + 1);
    if (tokenLength == 0 || tokenLength > 127) return FALSE;
    for (LPCWSTR character = colon + 1; *character; character++) {
        if (!((*character >= L'a' && *character <= L'z') || (*character >= L'A' && *character <= L'Z')
            || (*character >= L'0' && *character <= L'9'))) return FALSE;
    }
    *parentPid = (DWORD)value;
    wcscpy_s(token, 128, colon + 1);
    return TRUE;
}

// Quote each argument according to CreateProcess/CommandLineToArgvW rules, including a directory's
// trailing backslash. No shell, user-controlled command text or inherited handles are involved.
static BOOL AppendQuotedArgument(WCHAR *command, size_t capacity, size_t *length, LPCWSTR argument) {
    if (*length + 3 >= capacity) return FALSE;
    if (*length) command[(*length)++] = L' ';
    command[(*length)++] = L'"';
    size_t backslashes = 0;
    for (LPCWSTR character = argument; ; character++) {
        if (*character == L'\\') { backslashes++; continue; }
        size_t copies = (*character == L'"' || *character == L'\0') ? backslashes * 2 : backslashes;
        if (*length + copies + 3 >= capacity) return FALSE;
        while (copies--) command[(*length)++] = L'\\';
        backslashes = 0;
        if (*character == L'\0') break;
        if (*character == L'"') command[(*length)++] = L'\\';
        command[(*length)++] = *character;
    }
    command[(*length)++] = L'"';
    command[*length] = L'\0';
    return TRUE;
}

static BOOL PrepareCrashDirectory(WCHAR directory[ANALYZER_PATH_CAPACITY]) {
    if (!directory[0]) {
        WCHAR localAppData[WATCHDOG_PATH_CAPACITY];
        DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, WATCHDOG_PATH_CAPACITY);
        if (!length || length >= WATCHDOG_PATH_CAPACITY) return FALSE;
        if (_snwprintf_s(directory, ANALYZER_PATH_CAPACITY, _TRUNCATE, L"%s\\OpenTaskbar", localAppData) < 0)
            return FALSE;
        if (!CreateDirectoryW(directory, NULL) && GetLastError() != ERROR_ALREADY_EXISTS) return FALSE;
        if (wcscat_s(directory, ANALYZER_PATH_CAPACITY, L"\\Crashes") != 0) return FALSE;
    }
    if (!IsAbsolutePath(directory)) return FALSE;
    if (!CreateDirectoryW(directory, NULL) && GetLastError() != ERROR_ALREADY_EXISTS) return FALSE;
    DWORD attributes = GetFileAttributesW(directory);
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

static void WriteExitEvidence(LPCWSTR directory, DWORD parentPid, LPCSTR reason, BOOL hasExitCode, DWORD exitCode,
    SYSTEMTIME utc) {
    FILETIME fileTime;
    GetSystemTimeAsFileTime(&fileTime);
    ULARGE_INTEGER stamp;
    stamp.LowPart = fileTime.dwLowDateTime;
    stamp.HighPart = fileTime.dwHighDateTime;
    WCHAR path[ANALYZER_PATH_CAPACITY];
    WCHAR temporary[ANALYZER_PATH_CAPACITY];
    if (_snwprintf_s(path, ANALYZER_PATH_CAPACITY, _TRUNCATE, L"%s\\exit-%lu-%llu-%lu.json",
            directory, parentPid, stamp.QuadPart, GetCurrentProcessId()) < 0
        || _snwprintf_s(temporary, ANALYZER_PATH_CAPACITY, _TRUNCATE, L"%s.tmp", path) < 0) {
        DiagnosticWarning(L"OpenTaskbar watchdog: evidence path is too long.\n");
        return;
    }
    char code[16];
    if (hasExitCode) sprintf_s(code, sizeof(code), "\"0x%08lX\"", exitCode);
    else strcpy_s(code, sizeof(code), "null");
    char json[512];
    int length = sprintf_s(json, sizeof(json),
        "{\"pid\":%lu,\"utc\":\"%04u-%02u-%02uT%02u:%02u:%02u.%03u0000Z\",\"exitCode\":%s,\"reason\":\"%s\"}\n",
        parentPid, utc.wYear, utc.wMonth, utc.wDay, utc.wHour, utc.wMinute, utc.wSecond, utc.wMilliseconds, code, reason);
    if (length <= 0) return;
    HANDLE file = CreateFileW(temporary, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) {
        DiagnosticWarning(L"OpenTaskbar watchdog: exit evidence storage is unavailable.\n");
        return;
    }
    DWORD written = 0;
    BOOL complete = WriteFile(file, json, (DWORD)length, &written, NULL) && written == (DWORD)length;
    if (complete) complete = FlushFileBuffers(file);
    CloseHandle(file);
    if (complete) complete = MoveFileExW(temporary, path, MOVEFILE_WRITE_THROUGH);
    if (!complete) {
        DiagnosticWarning(L"OpenTaskbar watchdog: exit evidence could not be committed.\n");
        DeleteFileW(temporary);
    }
}

static void LaunchAnalyzer(LPCWSTR executable, LPCWSTR directory) {
    if (!IsAbsolutePath(executable)) {
        DiagnosticWarning(L"OpenTaskbar watchdog: analyzer executable is unavailable.\n");
        return;
    }
    LPCWSTR extension = wcsrchr(executable, L'.');
    DWORD attributes = GetFileAttributesW(executable);
    if (!extension || _wcsicmp(extension, L".exe") != 0 || attributes == INVALID_FILE_ATTRIBUTES
        || (attributes & FILE_ATTRIBUTE_DIRECTORY)) {
        DiagnosticWarning(L"OpenTaskbar watchdog: analyzer executable is invalid.\n");
        return;
    }
    WCHAR command[ANALYZER_COMMAND_CAPACITY] = {0};
    WCHAR directoryArgument[ANALYZER_PATH_CAPACITY];
    if (_snwprintf_s(directoryArgument, ANALYZER_PATH_CAPACITY, _TRUNCATE, L"--crash-directory=%s", directory) < 0)
        return;
    size_t length = 0;
    if (!AppendQuotedArgument(command, ANALYZER_COMMAND_CAPACITY, &length, executable)
        || !AppendQuotedArgument(command, ANALYZER_COMMAND_CAPACITY, &length, L"--analyze-crashes")
        || !AppendQuotedArgument(command, ANALYZER_COMMAND_CAPACITY, &length, directoryArgument)) {
        DiagnosticWarning(L"OpenTaskbar watchdog: analyzer arguments are too long.\n");
        return;
    }
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;
    if (CreateProcessW(executable, command, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &startup, &process)) {
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
    } else {
        DiagnosticWarning(L"OpenTaskbar watchdog: local analyzer could not start.\n");
    }
}

int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, LPSTR lpCmdLine, int nCmdShow) {
    (void)hInstance;
    (void)hPrevInstance;
    (void)lpCmdLine;
    (void)nCmdShow;
    AttachToDefaultDesktop();
    int argc = 0;
    LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 1;
    DWORD parentPid = 0;
    WCHAR token[128] = {0};
    WCHAR analyzer[ANALYZER_PATH_CAPACITY] = {0};
    WCHAR directory[ANALYZER_PATH_CAPACITY] = {0};
    BOOL valid = TRUE;
    for (int i = 1; i < argc; i++) {
        if (_wcsnicmp(argv[i], L"--watchdog=", 11) == 0) {
            if (parentPid || !ParseWatchdogArgument(argv[i] + 11, &parentPid, token)) valid = FALSE;
        } else if (_wcsnicmp(argv[i], L"--analyzer-exe=", 15) == 0) {
            if (analyzer[0] || !IsAbsolutePath(argv[i] + 15)
                || wcscpy_s(analyzer, ANALYZER_PATH_CAPACITY, argv[i] + 15) != 0) valid = FALSE;
        } else if (_wcsnicmp(argv[i], L"--crash-directory=", 18) == 0) {
            if (directory[0] || !IsAbsolutePath(argv[i] + 18)
                || wcscpy_s(directory, ANALYZER_PATH_CAPACITY, argv[i] + 18) != 0) valid = FALSE;
        }
    }
    LocalFree(argv);
    if (!valid || !parentPid || !token[0]) return 1;

    WCHAR eventName[256];
    _snwprintf_s(eventName, 256, _TRUNCATE, L"Local\\OpenTaskbar.CleanExit.%lu.%s", parentPid, token);
    HANDLE hCleanExit = OpenEventW(SYNCHRONIZE, FALSE, eventName);
    _snwprintf_s(eventName, 256, _TRUNCATE, L"Local\\OpenTaskbar.Heartbeat.%lu.%s", parentPid, token);
    HANDLE hHeartbeat = OpenEventW(SYNCHRONIZE, FALSE, eventName);
    HANDLE hProcess = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, parentPid);
    if (!hProcess) hProcess = OpenProcess(SYNCHRONIZE, FALSE, parentPid);

    BOOL cleanExit = hCleanExit && WaitForSingleObject(hCleanExit, 0) == WAIT_OBJECT_0;
    LPCSTR reason = "parent-unavailable";
    BOOL hasExitCode = FALSE;
    DWORD exitCode = 0;
    if (!cleanExit && hProcess) {
        HANDLE waitHandles[3];
        DWORD waitCount = 0;
        DWORD cleanIndex = MAXDWORD;
        DWORD heartbeatIndex = MAXDWORD;
        if (hCleanExit) { cleanIndex = waitCount; waitHandles[waitCount++] = hCleanExit; }
        DWORD processIndex = waitCount;
        waitHandles[waitCount++] = hProcess;
        if (hHeartbeat) { heartbeatIndex = waitCount; waitHandles[waitCount++] = hHeartbeat; }
        int heartbeatMisses = 0;
        reason = "watchdog-error";
        while (TRUE) {
            DWORD result = WaitForMultipleObjects(waitCount, waitHandles, FALSE, hHeartbeat ? HEARTBEAT_TIMEOUT_MS : INFINITE);
            if (hCleanExit && result == WAIT_OBJECT_0 + cleanIndex) { cleanExit = TRUE; break; }
            if (result == WAIT_OBJECT_0 + processIndex) {
                cleanExit = hCleanExit && WaitForSingleObject(hCleanExit, 0) == WAIT_OBJECT_0;
                reason = "process-exited";
                hasExitCode = GetExitCodeProcess(hProcess, &exitCode);
                break;
            }
            if (hHeartbeat && result == WAIT_OBJECT_0 + heartbeatIndex) { heartbeatMisses = 0; continue; }
            if (result == WAIT_TIMEOUT) {
                if (++heartbeatMisses < HEARTBEAT_MISSES_REQUIRED) continue;
                cleanExit = hCleanExit && WaitForSingleObject(hCleanExit, 0) == WAIT_OBJECT_0;
                // A timeout only proves absent heartbeats. The live dock is never killed or restarted.
                if (WaitForSingleObject(hProcess, 0) == WAIT_OBJECT_0) {
                    reason = "process-exited";
                    hasExitCode = GetExitCodeProcess(hProcess, &exitCode);
                } else reason = "heartbeat-timeout";
                break;
            }
            DiagnosticWarning(L"OpenTaskbar watchdog: waiting for the parent failed.\n");
            break;
        }
    }
    SYSTEMTIME observedUtc;
    GetSystemTime(&observedUtc);
    if (hHeartbeat) CloseHandle(hHeartbeat);
    if (hCleanExit) CloseHandle(hCleanExit);
    if (hProcess) CloseHandle(hProcess);
    if (!cleanExit) {
        RestoreNativeTaskbars();
        // This order is deliberate: neither unavailable storage nor a failed analyzer can block recovery.
        if (PrepareCrashDirectory(directory)) {
            WriteExitEvidence(directory, parentPid, reason, hasExitCode, exitCode, observedUtc);
            LaunchAnalyzer(analyzer, directory);
        } else {
            DiagnosticWarning(L"OpenTaskbar watchdog: crash directory is unavailable.\n");
        }
    }
    return 0;
}
