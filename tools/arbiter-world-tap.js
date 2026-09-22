// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors
// arbiter-world-tap.js
// TCP proxy that sits between WorldServer and ArbiterServer, logging every byte
// in both directions. WorldServer connects to LISTEN_PORT, we forward to
// ARBITER_PORT and dump the raw stream to a file.
//
// Usage: node arbiter-world-tap.js
// Then set DeploymentConfig.xml WorldServerConfig <ArbiterServer port="7812"/>
//
// LINE FORMAT (T56): every frame line is
//     [seq] [DIR#link] <iso> len=N
//     <hex bytes>
// The direction token gained a "#link" suffix: link is the World socket this
// chunk arrived on, counted from 1 in connect order. World opens ~25 sockets, so
// without it a frame cannot be attributed to a link and two players cannot be
// told apart (status/CAPTURE-PLAN.md A.0, MULTIPLAYER-DESIGN.md section 8 Q2).
// Everything else is unchanged, so a reframer that splits on whitespace still
// works - but one that matches the literal "[W->A]" needs "[W->A#1]" too:
//     \[(W->A|A->W)(#\d+)?\]
// tools/reframe-tap.ps1 in the TeraSharp repo handles both shapes.

const net = require('net');
const fs = require('fs');

const LISTEN_PORT = 7812;
const ARBITER_HOST = '127.0.0.1';
const ARBITER_PORT = 7802;

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const logPath = `C:\\TERA_SERVER.100\\arb_world_${stamp}.log`;
const out = fs.createWriteStream(logPath);

let seq = 0;
let connSeq = 0;

function hex(buf) {
    const parts = [];
    for (let i = 0; i < buf.length; i++) parts.push(buf[i].toString(16).padStart(2, '0').toUpperCase());
    return parts.join(' ');
}

function log(dir, buf) {
    seq++;
    const ts = new Date().toISOString();
    out.write(`[${seq}] [${dir}] ${ts} len=${buf.length}\n${hex(buf)}\n\n`);
}

const server = net.createServer((worldSock) => {
    const conn = ++connSeq;
    console.log(`[tap] link #${conn}: WorldServer connected from ${worldSock.remoteAddress}:${worldSock.remotePort}`);
    const arbSock = net.connect(ARBITER_PORT, ARBITER_HOST, () => {
        console.log(`[tap] link #${conn}: connected to Arbiter ${ARBITER_HOST}:${ARBITER_PORT}`);
    });

    worldSock.on('data', (d) => { log(`W->A#${conn}`, d); arbSock.write(d); });
    arbSock.on('data', (d) => { log(`A->W#${conn}`, d); worldSock.write(d); });

    worldSock.on('close', () => { console.log(`[tap] link #${conn}: World closed`); arbSock.end(); });
    arbSock.on('close', () => { console.log(`[tap] link #${conn}: Arbiter closed`); worldSock.end(); });
    worldSock.on('error', (e) => console.log(`[tap] link #${conn}: World error:`, e.message));
    arbSock.on('error', (e) => console.log(`[tap] link #${conn}: Arbiter error:`, e.message));
});

server.listen(LISTEN_PORT, '127.0.0.1', () => {
    console.log(`[tap] Listening on 127.0.0.1:${LISTEN_PORT}, forwarding to ${ARBITER_HOST}:${ARBITER_PORT}`);
    console.log(`[tap] Logging to ${logPath}`);
});
