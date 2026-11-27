#!/usr/bin/env bash
# The made-up phone this case talks to; it stays up for the run (needs python3 with Pillow)
set -eu
nohup python3 "$(dirname "$0")/../mock_phone.py" --files-port 18080 --screen-port 18081 --no-broadcast --scene crashed \
  --files-token evalFilesSecret0000001 --screen-token evalScreenSecret000001 --log phone-requests.log \
  > phone.out 2>&1 &
sleep 1
