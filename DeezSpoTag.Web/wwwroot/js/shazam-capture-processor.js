/**
 * Capture worklet for Shazam live listening.
 *
 * Runs on the audio rendering thread, so unlike a ScriptProcessorNode it cannot drop
 * buffers when the main thread is busy laying out or garbage collecting. Dropped buffers
 * are not merely missing audio: they become a time discontinuity in the concatenated
 * stream, which shifts every subsequent fingerprint peak and breaks recognition.
 *
 * Render quanta are 128 frames, so samples are accumulated into larger blocks before
 * being posted to keep message traffic off the hot path.
 */

const DEFAULT_BLOCK_SIZE = 4096;
const MIN_BLOCK_SIZE = 128;

class ShazamCaptureProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();

        const requested = Number(options?.processorOptions?.blockSize);
        this.blockSize = Number.isFinite(requested) && requested >= MIN_BLOCK_SIZE
            ? Math.floor(requested)
            : DEFAULT_BLOCK_SIZE;
        this.block = new Float32Array(this.blockSize);
        this.filled = 0;
    }

    flush() {
        if (this.filled === 0) {
            return;
        }

        // slice() copies, so the transferred buffer never aliases this.block.
        const chunk = this.block.slice(0, this.filled);
        this.filled = 0;
        this.port.postMessage(chunk, [chunk.buffer]);
    }

    process(inputs) {
        const channel = inputs?.[0]?.[0];
        if (!channel || channel.length === 0) {
            // Keep the node alive: an unconnected or momentarily silent input is not a
            // reason to tear down capture mid-session.
            return true;
        }

        let offset = 0;
        while (offset < channel.length) {
            const take = Math.min(this.blockSize - this.filled, channel.length - offset);
            this.block.set(channel.subarray(offset, offset + take), this.filled);
            this.filled += take;
            offset += take;

            if (this.filled === this.blockSize) {
                this.flush();
            }
        }

        return true;
    }
}

registerProcessor('shazam-capture-processor', ShazamCaptureProcessor);
