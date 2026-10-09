/** Types for the Emscripten build of libfvad (the WebRTC VAD) shipped by @echogarden/fvad-wasm. */
declare module '@echogarden/fvad-wasm' {
  export interface FvadModule {
    HEAP16: Int16Array;
    _malloc(bytes: number): number;
    _free(ptr: number): void;
    _fvad_new(): number;
    _fvad_free(inst: number): void;
    /** 0-3, higher = more aggressive about rejecting non-speech. Returns 0 on success. */
    _fvad_set_mode(inst: number, mode: number): number;
    /** 8000 | 16000 | 32000 | 48000. Returns 0 on success. */
    _fvad_set_sample_rate(inst: number, rate: number): number;
    /** 10/20/30 ms of mono s16 PCM. Returns 1 = speech, 0 = not speech, -1 = invalid frame. */
    _fvad_process(inst: number, framePtr: number, samples: number): number;
  }
  const createFvad: () => Promise<FvadModule>;
  export default createFvad;
}
