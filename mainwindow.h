#ifndef MAINWINDOW_H
#define MAINWINDOW_H

#include <QMainWindow>
#include <QTimer>
#include <QLabel>
#include <QPushButton>
#include <QVBoxLayout>
#include <QGroupBox>
#include <QPoint>

class MainWindow : public QMainWindow
{
    Q_OBJECT

public:
    MainWindow(QWidget *parent = nullptr);
    ~MainWindow();

private slots:
    void checkActiveWindow();
    void updateIdleTimer();
    void playAlertSound();
    void testSound();

private:
    bool isTargetWindowActive();
    QString getActiveWindowProcess();
    QPoint getMousePosition();
    void resetIdleTimer();
    void updateUI();
    QString formatTime(int seconds);

    // UI Components
    QLabel *statusLabel;
    QLabel *timerLabel;
    QLabel *activeWindowLabel;
    QPushButton *testSoundButton;

    // Timers
    QTimer *windowCheckTimer;
    QTimer *idleTimer;
    
    // State tracking
    bool targetWindowWasActive;
    int remainingSeconds;
    QPoint lastMousePos;
    bool isTimerRunning;
    
    // Constants
    static const int IDLE_TIMEOUT_SECONDS = 420; // 7 minutes = 420 seconds
    static const int WINDOW_CHECK_INTERVAL = 500; // Check every 500ms
    static const int IDLE_CHECK_INTERVAL = 1000; // Update every second
};

#endif // MAINWINDOW_H
