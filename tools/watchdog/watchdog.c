#define UNICODE
#define _UNICODE
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <stdlib.h>

#define ABS_AUTOHIDE    0x0000001
#define ABS_ALWAYSONTOP 0x0000002

static void AttachToDefaultDesktop(void) {
    HDESK hDesk = OpenDesktopW(L"Default", 0, FALSE, GENERIC_ALL);
    if (hDesk) {
        SetThreadDesktop(hDesk);
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

static void RestoreNativeTaskbars(void) {
    AttachToDefaultDesktop();

    // 1. Read journal if available to recover original auto-hide/always-on-top state
    WCHAR localAppData[MAX_PATH];
    DWORD state = 0;
    if (GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, MAX_PATH) > 0) {
        WCHAR journalPath[MAX_PATH];
        wsprintfW(journalPath, L"%s\\OpenTaskbar\\taskbar-state.txt", localAppData);
        FILE *jf = _wfopen(journalPath, L"r");
        if (jf) {
            unsigned int readVal = 0;
            if (fwscanf(jf, L"%u", &readVal) == 1) {
                state = (DWORD)readVal;
            }
            fclose(jf);
            DeleteFileW(journalPath);
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
}

int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, LPSTR lpCmdLine, int nCmdShow) {
    (void)hInstance;
    (void)hPrevInstance;
    (void)lpCmdLine;
    (void)nCmdShow;

    AttachToDefaultDesktop();

    int argc = 0;
    LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc < 2) return 1;

    int parentPid = 0;
    WCHAR token[128] = {0};

    for (int i = 1; i < argc; i++) {
        if (wcsncmp(argv[i], L"--watchdog=", 11) == 0) {
            LPCWSTR data = argv[i] + 11;
            LPCWSTR colon = wcschr(data, L':');
            if (colon && colon > data) {
                parentPid = _wtoi(data);
                wcsncpy_s(token, 128, colon + 1, _TRUNCATE);
                break;
            }
        }
    }
    LocalFree(argv);

    if (parentPid <= 0 || token[0] == L'\0') {
        return 1;
    }

    HANDLE hProcess = OpenProcess(SYNCHRONIZE, FALSE, (DWORD)parentPid);
    if (!hProcess) {
        RestoreNativeTaskbars();
        return 0;
    }

    WCHAR eventName[256];
    wsprintfW(eventName, L"Local\\OpenTaskbar.CleanExit.%d.%s", parentPid, token);
    HANDLE hCleanExit = OpenEventW(SYNCHRONIZE, FALSE, eventName);

    HANDLE waitHandles[2];
    DWORD waitCount = 0;

    if (hCleanExit) {
        waitHandles[0] = hCleanExit;
        waitHandles[1] = hProcess;
        waitCount = 2;
    } else {
        waitHandles[0] = hProcess;
        waitCount = 1;
    }

    DWORD waitResult = WaitForMultipleObjects(waitCount, waitHandles, FALSE, INFINITE);

    BOOL cleanExit = FALSE;
    if (waitCount == 2) {
        if (waitResult == WAIT_OBJECT_0) {
            cleanExit = TRUE;
        } else if (waitResult == WAIT_OBJECT_0 + 1) {
            if (WaitForSingleObject(hCleanExit, 0) == WAIT_OBJECT_0) {
                cleanExit = TRUE;
            }
        }
    }

    if (hCleanExit) CloseHandle(hCleanExit);
    CloseHandle(hProcess);

    if (!cleanExit) {
        RestoreNativeTaskbars();
    }

    return 0;
}
