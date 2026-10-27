import assert from 'node:assert/strict';
import test from 'node:test';
import {
    encodeGuacamoleInstruction,
    GuacamoleInstructionParser
} from '../src/MeshAll.Server/wwwroot/remote/guacamole-protocol.mjs';

test('Guacamole instruction lengths count Unicode characters, including supplementary characters', () => {
    assert.equal(encodeGuacamoleInstruction(['name', 'A💻é']), '4.name,3.A💻é;');
    assert.equal(
        encodeGuacamoleInstruction(['clipboard', 0, 'text/plain']),
        '9.clipboard,1.0,10.text/plain;'
    );
});

test('fragmented Unicode, separators inside values and adjacent instructions decode without loss', () => {
    const calls = [];
    const parser = new GuacamoleInstructionParser();
    parser.oninstruction = (...args) => calls.push(args);
    const wire =
        encodeGuacamoleInstruction(['name', 'A💻é,;']) + encodeGuacamoleInstruction(['sync', 123, '']);
    for (const unit of wire.split('')) parser.receive(unit); // Split the surrogate pair too.
    assert.deepEqual(calls, [
        ['name', ['A💻é,;']],
        ['sync', ['123', '']]
    ]);
});

for (const wire of ['.value;', 'x.name;', '-1.x;', '9999999.x;', '1.x!', '0.;', '1.\udc00;', '1.\ud800x;']) {
    test(`malformed Guacamole framing is rejected: ${JSON.stringify(wire)}`, () => {
        const parser = new GuacamoleInstructionParser();
        assert.throws(() => parser.receive(wire));
    });
}

test('a large fragmented graphical payload is delivered once within the framing bounds', () => {
    const parser = new GuacamoleInstructionParser();
    const calls = [];
    parser.oninstruction = (...args) => calls.push(args);
    const payload = 'A'.repeat(64 * 1024);
    const wire = encodeGuacamoleInstruction(['blob', 0, payload]);
    for (let start = 0; start < wire.length; start += 4096) parser.receive(wire.slice(start, start + 4096));
    assert.deepEqual(calls, [['blob', ['0', payload]]]);
});
