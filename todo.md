Bug 1:
Win-to-win test fail sometimes( especially the first time), which might be caused by wrong role identified( they always work connecting to android (flutter) side):

We should:
In Windows ConnectToDeviceAsync and accepted incoming connection flow, wait/retry until GetConnectionEndpointPairs() returns valid IPv4 local/remote endpoints before raising DeviceConnected.
If ConnectionStatusChanged fires Connected, refresh endpoint pairs there too.
Do not let SessionManager start until LocalIP, RemoteIP, and role inference are ready.

This is a basic assumption, you should dig in more if needed.

Bug 2:
When audio is already connected, the status is correct, but if I switch tab and then switch back, the status likely reset and show 'idle' even it's streaming.

Bug 3:
Sometimes the chat message will not show, if I haven't enter chat tab after connected, maybe the service is not initialized or other reason?
