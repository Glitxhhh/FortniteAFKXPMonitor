QT += core gui widgets

CONFIG += c++17

TARGET = ActivityMonitor
TEMPLATE = app

SOURCES += \
    main.cpp \
    mainwindow.cpp

HEADERS += \
    mainwindow.h

win32:RC_FILE = app.rc

LIBS += -luser32 -lwinmm

# Install
target.path = $$[QT_INSTALL_EXAMPLES]/ActivityMonitor
INSTALLS += target
