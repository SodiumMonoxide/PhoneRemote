"""Local pairing code shared by the PC console and display host."""

import hashlib
import hmac
import os
import secrets
import tempfile
from datetime import datetime, time, timedelta, timezone
from pathlib import Path


BEIJING = timezone(timedelta(hours=8))


def load_daily_key(state_dir: Path) -> bytes:
    state_dir = Path(state_dir)
    key_path = state_dir / "daily-key.bin"
    try:
        key = key_path.read_bytes()
    except FileNotFoundError:
        state_dir.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(dir=state_dir, prefix=".daily-key-", delete=False) as f:
            temporary = Path(f.name)
        try:
            with temporary.open("wb") as f:
                f.write(secrets.token_bytes(32))
                f.flush()
                os.fsync(f.fileno())
            try:
                # Publish a complete key only if there is no existing entry.
                # Competing console / host processes always read the winner.
                os.link(temporary, key_path)
            except FileExistsError:
                pass
            key = key_path.read_bytes()
        finally:
            temporary.unlink(missing_ok=True)
    if len(key) != 32:
        raise ValueError("Invalid daily pairing key")
    return key


def beijing_day(now: datetime | None = None):
    if now is None:
        now = datetime.now(BEIJING)
    if now.utcoffset() is None:
        raise ValueError("An explicitly supplied pairing time must include a time zone")
    return now.astimezone(BEIJING).date()


def daily_code(key: bytes, now: datetime | None = None) -> str:
    date = beijing_day(now).isoformat().encode("ascii")
    digest = hmac.new(key, b"ipad-wifi-display/daily-pin/v1/" + date, hashlib.sha256).digest()
    return f"{int.from_bytes(digest[:8], 'big') % 1_000_000:06d}"


def daily_expiry(now: datetime | None = None) -> datetime:
    return datetime.combine(beijing_day(now) + timedelta(days=1), time.min, tzinfo=BEIJING)
