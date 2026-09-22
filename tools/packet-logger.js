// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

'use strict';

const fs = require('fs');
const path = require('path');

module.exports = function PacketLogger(mod) {
    const logDir = path.join(__dirname, '..', '..', 'packet-logs');
    if (!fs.existsSync(logDir)) fs.mkdirSync(logDir, { recursive: true });

    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const logPath = path.join(logDir, `capture_${stamp}.log`);

    let seq = 0;
    let buffer = '';

    function flush() {
        if (buffer.length > 0) {
            fs.appendFileSync(logPath, buffer);
            buffer = '';
        }
    }

    setInterval(flush, 2000);

    console.log(`[packet-logger] logging ALL packets to ${logPath}`);

    mod.hook('*', 'raw', { order: -9999, filter: { fake: false } }, (code, data, incoming) => {
        seq++;
        const dir = incoming ? 'S->C' : 'C->S';
        let name;
        try {
            name = mod.dispatch.protocolMap.code.get(code) || `opcode_${code}`;
        } catch(e) {
            name = `opcode_${code}`;
        }

        // Full hex, no truncation
        let hex = '';
        for (let i = 0; i < data.length; i++) {
            hex += data[i].toString(16).padStart(2, '0').toUpperCase() + ' ';
        }

        buffer += `[${seq}] [${dir}] ${name} (${code}) len=${data.length}\nHEX: ${hex.trim()}\n\n`;
    });
};
