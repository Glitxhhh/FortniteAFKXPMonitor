#include "mainwindow.h"
#include <QVBoxLayout>
#include <QGroupBox>
#include <QMessageBox>
#include <QCursor>
#include <QIcon>

#ifdef Q_OS_WIN
#include <windows.h>
#include <tlhelp32.h>
#include <psapi.h>
#pragma comment(lib, "winmm.lib")
#endif

MainWindow::MainWindow(QWidget *parent)
    : QMainWindow(parent)
    , targetWindowWasActive(false)
    , remainingSeconds(IDLE_TIMEOUT_SECONDS)
    , isTimerRunning(false)
{
    setWindowTitle("Fortnite AFK XP Tool");
    setMinimumSize(500, 400);
    resize(550, 450);
    
    // Set window icon if file exists
    QIcon appIcon("icon.ico");
    if (!appIcon.isNull()) {
        setWindowIcon(appIcon);
    }
    // If icon.ico doesn't exist, just use default Windows icon

    // Create central widget and layout
    QWidget *centralWidget = new QWidget(this);
    QVBoxLayout *mainLayout = new QVBoxLayout(centralWidget);
    mainLayout->setSpacing(15);
    mainLayout->setContentsMargins(20, 20, 20, 20);

    // Apply modern stylesheet
    setStyleSheet(
        "QMainWindow { background-color: #2b2b2b; }"
        "QGroupBox { "
        "    color: #ffffff; "
        "    border: 2px solid #4a90e2; "
        "    border-radius: 8px; "
        "    margin-top: 12px; "
        "    padding-top: 15px; "
        "    font-weight: bold; "
        "    font-size: 13px; "
        "}"
        "QGroupBox::title { "
        "    subcontrol-origin: margin; "
        "    subcontrol-position: top center; "
        "    padding: 5px 15px; "
        "    background-color: #4a90e2; "
        "    border-radius: 4px; "
        "}"
        "QLabel { "
        "    color: #e0e0e0; "
        "    font-size: 12px; "
        "    padding: 5px; "
        "}"
        "QPushButton { "
        "    background-color: #4a90e2; "
        "    color: white; "
        "    border: none; "
        "    border-radius: 6px; "
        "    padding: 12px 24px; "
        "    font-size: 13px; "
        "    font-weight: bold; "
        "}"
        "QPushButton:hover { "
        "    background-color: #5da3f5; "
        "}"
        "QPushButton:pressed { "
        "    background-color: #3a7bc8; "
        "}"
    );

    // Title and info section
    QLabel *titleLabel = new QLabel("🎮 Fortnite AFK XP Monitor", this);
    titleLabel->setStyleSheet(
        "font-size: 20px; "
        "font-weight: bold; "
        "color: #4a90e2; "
        "padding: 10px; "
        "background-color: #1e1e1e; "
        "border-radius: 8px;"
    );
    titleLabel->setAlignment(Qt::AlignCenter);
    mainLayout->addWidget(titleLabel);

    // Info panel
    QLabel *infoLabel = new QLabel(
        "💡 This tool monitors your activity and alerts you after 7 minutes away from Fortnite.\n"
        "    Perfect for keeping your AFK XP session active!", this);
    infoLabel->setStyleSheet(
        "color: #b0b0b0; "
        "font-size: 11px; "
        "padding: 12px; "
        "background-color: #1e1e1e; "
        "border-radius: 6px; "
        "border-left: 3px solid #4a90e2;"
    );
    infoLabel->setWordWrap(true);
    mainLayout->addWidget(infoLabel);

    // Status Group
    QGroupBox *statusGroup = new QGroupBox("📊 Current Status", this);
    QVBoxLayout *statusLayout = new QVBoxLayout(statusGroup);
    statusLayout->setSpacing(10);
    
    activeWindowLabel = new QLabel("Active Window: Initializing...", this);
    activeWindowLabel->setStyleSheet("font-size: 13px; font-weight: bold; color: #ffffff;");
    
    statusLabel = new QLabel("Status: Starting up...", this);
    statusLabel->setStyleSheet("font-size: 13px; padding: 8px; border-radius: 4px;");
    
    timerLabel = new QLabel("Timer: Not Started", this);
    timerLabel->setStyleSheet("font-size: 16px; font-weight: bold; padding: 8px; border-radius: 4px;");
    
    statusLayout->addWidget(activeWindowLabel);
    statusLayout->addWidget(statusLabel);
    statusLayout->addWidget(timerLabel);
    statusLayout->addStretch();

    // Monitored apps info
    QGroupBox *appsGroup = new QGroupBox("🎯 Monitored Applications", this);
    QVBoxLayout *appsLayout = new QVBoxLayout(appsGroup);
    
    QLabel *appsInfo = new QLabel(
        "✓ Fortnite (Game Client)\n"
        "✓ GeForce NOW (Cloud Gaming)\n"
        "✓ Chrome (Amazon Luna)", this);
    appsInfo->setStyleSheet("color: #a0a0a0; font-size: 11px; padding: 5px;");
    appsLayout->addWidget(appsInfo);

    // Control buttons
    QHBoxLayout *buttonLayout = new QHBoxLayout();
    buttonLayout->setSpacing(10);
    
    testSoundButton = new QPushButton("🔊 Test Alert Sound", this);
    connect(testSoundButton, &QPushButton::clicked, this, &MainWindow::testSound);
    
    buttonLayout->addWidget(testSoundButton);
    buttonLayout->addStretch();

    // Add everything to main layout
    mainLayout->addWidget(statusGroup, 1);
    mainLayout->addWidget(appsGroup);
    mainLayout->addLayout(buttonLayout);

    // Footer
    QLabel *footerLabel = new QLabel("v1.0 | Active monitoring in progress", this);
    footerLabel->setStyleSheet(
        "color: #606060; "
        "font-size: 10px; "
        "padding: 8px;"
    );
    footerLabel->setAlignment(Qt::AlignCenter);
    mainLayout->addWidget(footerLabel);

    setCentralWidget(centralWidget);

    // Initialize timers
    windowCheckTimer = new QTimer(this);
    connect(windowCheckTimer, &QTimer::timeout, this, &MainWindow::checkActiveWindow);
    windowCheckTimer->start(WINDOW_CHECK_INTERVAL);

    idleTimer = new QTimer(this);
    connect(idleTimer, &QTimer::timeout, this, &MainWindow::updateIdleTimer);

    // Get initial mouse position
    lastMousePos = getMousePosition();

    // Initial check
    checkActiveWindow();
}

MainWindow::~MainWindow()
{
}

#ifdef Q_OS_WIN
QString MainWindow::getActiveWindowProcess()
{
    HWND hwnd = GetForegroundWindow();
    if (hwnd == NULL)
        return "Unknown";

    DWORD processId;
    GetWindowThreadProcessId(hwnd, &processId);

    HANDLE hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, processId);
    if (hProcess == NULL)
        return "Unknown";

    char processName[MAX_PATH] = {0};
    HMODULE hMod;
    DWORD cbNeeded;

    if (EnumProcessModules(hProcess, &hMod, sizeof(hMod), &cbNeeded))
    {
        GetModuleBaseNameA(hProcess, hMod, processName, sizeof(processName));
    }

    CloseHandle(hProcess);
    
    return QString::fromLocal8Bit(processName);
}
#else
QString MainWindow::getActiveWindowProcess()
{
    return "Linux (not implemented)";
}
#endif

bool MainWindow::isTargetWindowActive()
{
    QString processName = getActiveWindowProcess().toLower();
    return (processName == "geforcenow.exe" || 
            processName == "chrome.exe" ||
            processName == "fortniteclient-win64-shipping.exe");
}

QPoint MainWindow::getMousePosition()
{
    return QCursor::pos();
}

void MainWindow::checkActiveWindow()
{
    bool isActive = isTargetWindowActive();
    QString activeProcess = getActiveWindowProcess();
    
    // Format active window display nicely
    QString displayName = activeProcess;
    if (activeProcess.toLower() == "fortniteclient-win64-shipping.exe") {
        displayName = "🎮 Fortnite (Game Client)";
    } else if (activeProcess.toLower() == "geforcenow.exe") {
        displayName = "☁️ GeForce NOW";
    } else if (activeProcess.toLower() == "chrome.exe") {
        displayName = "🌐 Chrome (Luna)";
    } else {
        displayName = "❌ " + activeProcess;
    }
    
    activeWindowLabel->setText("Active Window: " + displayName);

    if (isActive)
    {
        // Target window is active - reset timer (user is using the app)
        remainingSeconds = IDLE_TIMEOUT_SECONDS;
        
        if (isTimerRunning)
        {
            idleTimer->stop();
            isTimerRunning = false;
        }
        
        statusLabel->setText("✅ Status: Playing Fortnite - Timer Reset");
        statusLabel->setStyleSheet(
            "font-size: 13px; "
            "padding: 10px; "
            "border-radius: 4px; "
            "background-color: #2d5016; "
            "color: #90ee90; "
            "font-weight: bold;"
        );
        
        // Update last mouse position while active
        lastMousePos = getMousePosition();
    }
    else
    {
        // Target window is NOT active - start countdown if not already running
        if (!isTimerRunning)
        {
            idleTimer->start(IDLE_CHECK_INTERVAL);
            isTimerRunning = true;
        }
        
        statusLabel->setText("⏳ Status: Away from Fortnite - Counting Down");
        statusLabel->setStyleSheet(
            "font-size: 13px; "
            "padding: 10px; "
            "border-radius: 4px; "
            "background-color: #5c3d1a; "
            "color: #ffb347; "
            "font-weight: bold;"
        );
    }

    updateUI();
    targetWindowWasActive = isActive;
}

void MainWindow::updateIdleTimer()
{
    // Timer is running - just count down
    // Mouse movement is ignored when target window is not active
    remainingSeconds--;
    
    if (remainingSeconds <= 0)
    {
        // Timer expired - play alert
        playAlertSound();
        // Reset timer to continue monitoring
        remainingSeconds = IDLE_TIMEOUT_SECONDS;
    }

    updateUI();
}

void MainWindow::updateUI()
{
    if (isTimerRunning)
    {
        QString timeStr = formatTime(remainingSeconds);
        timerLabel->setText("⏱️ Time Until Alert: " + timeStr);
        
        // Dynamic color based on urgency
        if (remainingSeconds <= 60) {
            // Critical - less than 1 minute
            timerLabel->setStyleSheet(
                "font-size: 18px; "
                "font-weight: bold; "
                "padding: 12px; "
                "border-radius: 6px; "
                "background-color: #5c1a1a; "
                "color: #ff6b6b; "
                "border: 2px solid #ff6b6b;"
            );
        } else if (remainingSeconds <= 180) {
            // Warning - less than 3 minutes
            timerLabel->setStyleSheet(
                "font-size: 18px; "
                "font-weight: bold; "
                "padding: 12px; "
                "border-radius: 6px; "
                "background-color: #5c3d1a; "
                "color: #ffb347; "
                "border: 2px solid #ffb347;"
            );
        } else {
            // Normal - more than 3 minutes
            timerLabel->setStyleSheet(
                "font-size: 18px; "
                "font-weight: bold; "
                "padding: 12px; "
                "border-radius: 6px; "
                "background-color: #1a3a5c; "
                "color: #5da3f5; "
                "border: 2px solid #5da3f5;"
            );
        }
    }
    else
    {
        timerLabel->setText("✅ Timer: Inactive (Playing Fortnite)");
        timerLabel->setStyleSheet(
            "font-size: 16px; "
            "font-weight: bold; "
            "padding: 12px; "
            "border-radius: 6px; "
            "background-color: #2d5016; "
            "color: #90ee90; "
            "border: 2px solid #90ee90;"
        );
    }
}

QString MainWindow::formatTime(int seconds)
{
    int mins = seconds / 60;
    int secs = seconds % 60;
    return QString("%1:%2").arg(mins).arg(secs, 2, 10, QChar('0'));
}

void MainWindow::playAlertSound()
{
    statusLabel->setText("🚨 ALERT: Away from Fortnite for 7 Minutes!");
    statusLabel->setStyleSheet(
        "font-size: 14px; "
        "padding: 12px; "
        "border-radius: 4px; "
        "background-color: #5c1a1a; "
        "color: #ff6b6b; "
        "font-weight: bold; "
        "border: 2px solid #ff6b6b;"
    );

#ifdef Q_OS_WIN
    // Play Windows system critical beep
    MessageBeep(MB_ICONHAND);
    Sleep(100);
    
    // Play a series of beeps for attention
    Beep(1000, 300);  // 1000 Hz for 300ms
    Sleep(100);
    Beep(1000, 300);
    Sleep(100);
    Beep(1000, 300);
    
    // Also try to play system sound
    PlaySound(TEXT("SystemExclamation"), NULL, SND_ALIAS | SND_ASYNC);
#endif

    // Show message box as well
    QMessageBox msgBox(this);
    msgBox.setWindowTitle("⚠️ AFK Alert");
    msgBox.setIcon(QMessageBox::Warning);
    msgBox.setText("You've been away from Fortnite for 7 minutes!");
    msgBox.setInformativeText(
        "Return to one of these to reset the timer:\n\n"
        "• Fortnite Game Client\n"
        "• GeForce NOW\n"
        "• Chrome (Amazon Luna)\n\n"
        "Keep your AFK session active!"
    );
    msgBox.setStandardButtons(QMessageBox::Ok);
    msgBox.setDefaultButton(QMessageBox::Ok);
    msgBox.exec();

    // Reset status after a delay
    QTimer::singleShot(3000, this, [this]() {
        if (!isTargetWindowActive()) {
            statusLabel->setText("⏳ Status: Away from Fortnite - Counting Down");
            statusLabel->setStyleSheet(
                "font-size: 13px; "
                "padding: 10px; "
                "border-radius: 4px; "
                "background-color: #5c3d1a; "
                "color: #ffb347; "
                "font-weight: bold;"
            );
        }
    });
}

void MainWindow::testSound()
{
#ifdef Q_OS_WIN
    MessageBeep(MB_ICONWARNING);
    Sleep(100);
    Beep(800, 500);
    PlaySound(TEXT("SystemAsterisk"), NULL, SND_ALIAS | SND_ASYNC);
#endif
    
    QMessageBox::information(this, "🔊 Sound Test", 
        "Alert sound played successfully!\n\n"
        "This is what you'll hear when the 7-minute timer expires.");
}

void MainWindow::resetIdleTimer()
{
    remainingSeconds = IDLE_TIMEOUT_SECONDS;
    lastMousePos = getMousePosition();
}
