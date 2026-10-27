// The wire prefix counts Unicode scalar values, not UTF-8 bytes or UTF-16 code units.
export function encodeGuacamoleInstruction(parts) {
    return (
        parts
            .map((part) => {
                const value = String(part);
                return [...value].length + '.' + value;
            })
            .join(',') + ';'
    );
}

/** Incremental first-party framing; retained Guacamole.Client still owns rendering/input. */
export class GuacamoleInstructionParser {
    constructor() {
        this.buffer = '';
        this.position = 0;
        this.length = null;
        this.arguments = [];
        this.instructionSize = 0;
    }

    receive(packet) {
        this.buffer += packet;
        if (this.buffer.length + this.instructionSize > 4 * 1024 * 1024)
            throw Error('Remote protocol instruction is too large.');

        while (true) {
            if (this.length === null) {
                const end = this.buffer.indexOf('.', this.position);
                if (end < 0) {
                    const prefix = this.buffer.slice(this.position);
                    if (!/^\d{0,7}$/.test(prefix)) throw Error('Invalid remote protocol length.');
                    return;
                }
                const prefix = this.buffer.slice(this.position, end);
                if (!/^\d{1,7}$/.test(prefix) || Number(prefix) > 1024 * 1024)
                    throw Error('Invalid remote protocol length.');
                this.length = Number(prefix);
                this.start = this.scan = end + 1;
                this.characters = 0;
            }

            while (this.characters < this.length) {
                if (this.scan >= this.buffer.length) return;
                const first = this.buffer.charCodeAt(this.scan);
                if (first >= 0xd800 && first <= 0xdbff) {
                    if (this.scan + 1 >= this.buffer.length) return;
                    const second = this.buffer.charCodeAt(this.scan + 1);
                    if (second < 0xdc00 || second > 0xdfff) throw Error('Invalid remote protocol Unicode.');
                    this.scan += 2;
                } else {
                    if (first >= 0xdc00 && first <= 0xdfff) throw Error('Invalid remote protocol Unicode.');
                    this.scan++;
                }
                this.characters++;
            }
            if (this.scan >= this.buffer.length) return;
            const separator = this.buffer[this.scan];
            if (separator !== ',' && separator !== ';') throw Error('Invalid remote protocol separator.');
            const value = this.buffer.slice(this.start, this.scan);
            this.instructionSize += value.length;
            this.arguments.push(value);
            if (this.arguments.length > 512 || this.instructionSize > 4 * 1024 * 1024)
                throw Error('Remote protocol instruction is too large.');
            this.position = this.scan + 1;
            this.length = null;
            if (this.position > 8192 || this.position === this.buffer.length) {
                this.buffer = this.buffer.slice(this.position);
                this.position = 0;
            }
            if (separator === ';') {
                const [opcode, ...args] = this.arguments;
                if (!opcode) throw Error('Invalid remote protocol opcode.');
                this.arguments = [];
                this.instructionSize = 0;
                this.oninstruction?.(opcode, args);
            }
        }
    }
}
