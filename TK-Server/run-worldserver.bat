set /p port=Enter port:
docker run  -m 32GB --restart unless-stopped --network redis --name WorldServer-%Port% -e IS_DOCKER=true -p %port%:2000 -d -v tkr_resources:/data tkr-worldserver /data/%Port%.json