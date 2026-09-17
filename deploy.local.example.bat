@echo off
rem  Copy to deploy.local.bat (gitignored) next to deploy.bat and fill in real values.
rem  deploy.bat calls it after elevation; any KC_* setting from deploy.bat can be overridden.
rem  KC_BACKEND_URL is required: the fleet backend URL, or "standalone" for the built-in bot.
set "KC_BACKEND_URL=https://kidcontrol.example.com"
