QQ Reborn — bundled NapCat OneBot defaults
==========================================

HTTP API : 127.0.0.1:3000
Event WS : 127.0.0.1:3001
Token    : (empty)

These files are applied by QQ Reborn 管家 when it stages NapCat into
%LocalAppData%\QQReborn\NapCat (writable copy). RealServer connects to the
same endpoints via NAPCAT_HTTP / NAPCAT_WS.

Do not expose ports 3000/3001 to the public internet. Only map RealServer :8765.
