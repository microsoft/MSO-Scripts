WebIO (the core of the WinHTTP Network Stack)
Session -> Request -> Connection -> Socket -> (TCB)

There may be multiple Requests per Session.
Connections may be shared/reused across Requests.
Here, separate/linked Connection objects represent a shared Connection, one copy per Request.
A Connection can link to several Sockets, when several IP addresses correspond to a server.
Each socket is tied to a TCB: Transfer Control Block (TcpIp)

Session1
  Request1 <- Connection1a -> Socket1a -> TCB1
                   ^        / Socket1b -> TCB2
Session2           |       /  Socket1c -> TCB3
  Request2 <- Connection1b/

Session1
  Request3 <- Connection2  -> Socket2  -> TCB4


Session    Request  Connection   Socket       |  TCB
  ID         ID         ID         ID         |   ID
 Times      Times     Times      Times        | Times
iReqFirst->  URL ----- URLx        --         |  PID
 XLink ---- XLink  <-iRequest     iTCB->      |  TID
         <-iSession    PID   ^    TID         | cbSend
            Method   iCxnNext \  <-iCxn       | cbRecv
                       iDNS       iAddr    ^  |
                  iSocketFirst->iSocketNext \ |
                      cbSend
                      cbRecv

XLink = callstack info which is also linked to their invoking threadpool objects
iDNS = index into the DNS table: server & various IP Addresses
iAddr = index into the IP Address list of a DNS entry
iSocketFirst/Next = linked list (by index) of related Sockets
iCxn/Next = linked list (by index) of Connection objects which represent a connection shared across Request objects

Traversal links look like this, with Sockets and Connections also linking themselves into subgroups.
TCB <- Socket[<] -> Connection[<] -> Request -> Session
