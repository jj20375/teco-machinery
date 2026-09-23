#!/bin/bash
# 在 Ubuntu Server 24.04 VM 內執行。這是 Docker Engine（Apache-2.0，商用免費），
# 不是 Docker Desktop——兩者是不同產品，只有 Desktop 的 GUI 對大型企業要收費。
set -euo pipefail

echo "== 更新系統 =="
sudo apt-get update && sudo apt-get -y upgrade

echo "== 安裝 Docker Engine CE =="
curl -fsSL https://get.docker.com | sudo sh

echo "== 讓一般使用者可以不用 sudo 執行 docker =="
sudo usermod -aG docker "$USER"
echo "請登出重新登入，讓群組設定生效（或執行 newgrp docker）"

echo "== 安裝 docker compose plugin（get.docker.com 通常已含，這裡確認一次）=="
docker compose version || sudo apt-get install -y docker-compose-plugin

echo "== SSH 強化：只允許金鑰登入、關閉 root 登入 =="
sudo sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config
sudo sed -i 's/^#\?PermitRootLogin.*/PermitRootLogin no/' /etc/ssh/sshd_config
sudo systemctl restart ssh

echo "== 安裝 fail2ban 防暴力破解 =="
sudo apt-get install -y fail2ban
sudo systemctl enable --now fail2ban

echo "== 設定時區 =="
sudo timedatectl set-timezone Asia/Taipei

echo "== 開機自動啟動 docker daemon（get.docker.com 預設已啟用，這裡確認一次）=="
sudo systemctl enable docker

echo "完成。docker --version / docker compose version 確認安裝結果："
docker --version
docker compose version
