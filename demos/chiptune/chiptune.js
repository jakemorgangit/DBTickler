/*
 * chiptune.js — a tiny NES-style music and sound-effect engine for browser games.
 *
 * Everything is synthesised live with the Web Audio API: no audio files, no dependencies.
 * Four channels mirror the NES sound chip:
 *   pulse1, pulse2  square waves with a selectable duty cycle (12.5%, 25%, 50%, 75%)
 *   triangle        the bass channel
 *   noise           drums (k = kick, s = snare, h = closed hat, o = open hat, c = crash)
 *
 * Usage:
 *   const chip = new ChipTune();
 *   button.onclick = () => chip.unlock();   // browsers only allow audio after a user gesture
 *   chip.play(ChipTune.songs.overworld);
 *   chip.sfx('coin');
 *   chip.stop();
 *
 * Song format: each pitched channel is a line of "NOTE:steps" tokens, where a step is a
 * sixteenth note and "r" is a rest, e.g. "C5:2 E5:2 G5:4 r:8". Drum lines use one character
 * per step and "." for silence, e.g. "k.h.s.h.". Shorter lines repeat to fill the song.
 */
(function (global) {
  'use strict';

  const CHANNELS = ['pulse1', 'pulse2', 'triangle', 'noise'];
  const PITCH = { C: 0, D: 2, E: 4, F: 5, G: 7, A: 9, B: 11 };
  const NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];

  function noteToMidi(name) {
    const m = /^([A-G])([#b]?)(\d)$/.exec(name);
    if (!m) throw new Error('Unknown note: ' + name);
    const accidental = m[2] === '#' ? 1 : m[2] === 'b' ? -1 : 0;
    return (Number(m[3]) + 1) * 12 + PITCH[m[1]] + accidental;
  }
  const midiToNote = (midi) => NAMES[midi % 12] + (Math.floor(midi / 12) - 1);
  const midiToFreq = (midi) => 440 * Math.pow(2, (midi - 69) / 12);
  const transpose = (note, semitones) => midiToNote(noteToMidi(note) + semitones);

  function parseLine(src) {
    const events = [];
    let step = 0;
    for (const token of src.trim().split(/\s+/)) {
      const [note, len] = token.split(':');
      const steps = len ? Number(len) : 1;
      if (note !== 'r') events.push({ step, note, len: steps });
      step += steps;
    }
    return { events, length: step };
  }

  function parseDrums(src) {
    const chars = src.replace(/[\s|]/g, '');
    const events = [];
    for (let i = 0; i < chars.length; i++) {
      if (chars[i] !== '.') events.push({ step: i, note: chars[i], len: 1 });
    }
    return { events, length: chars.length };
  }

  // Turns a song definition into a per-step event table plus tracker rows for display.
  function compile(song) {
    const parsed = {};
    let length = 0;
    for (const ch of CHANNELS) {
      if (!song[ch]) continue;
      parsed[ch] = ch === 'noise' ? parseDrums(song[ch].line) : parseLine(song[ch].line);
      length = Math.max(length, parsed[ch].length);
    }
    const byStep = Array.from({ length }, () => []);
    const rows = Array.from({ length }, () => ({}));
    for (const ch in parsed) {
      const { events, length: lineLength } = parsed[ch];
      const delay = song[ch].delay || 0;
      for (let rep = 0; rep < length; rep += lineLength) {
        for (const e of events) {
          const s = (e.step + rep + delay) % length;
          const freq = ch === 'noise' ? 0 : midiToFreq(noteToMidi(e.note));
          byStep[s].push({ ch, note: e.note, len: e.len, freq });
          rows[s][ch] = e.note;
          for (let k = 1; k < e.len; k++) {
            const h = (s + k) % length;
            if (!rows[h][ch]) rows[h][ch] = '~';
          }
        }
      }
    }
    return { name: song.name, bpm: song.bpm, length, byStep, rows, def: song };
  }

  // Band-limited pulse wave with the given duty cycle, built from its Fourier series.
  function pulseWave(ctx, duty) {
    const n = 64;
    const real = new Float32Array(n);
    const imag = new Float32Array(n);
    for (let k = 1; k < n; k++) real[k] = (2 / (k * Math.PI)) * Math.sin(k * Math.PI * duty);
    return ctx.createPeriodicWave(real, imag);
  }

  class ChipTune {
    constructor() {
      this.ctx = null;
      this.song = null;
      this.playing = false;
      this.muted = {};
      this.listeners = new Set();
    }

    _init() {
      if (this.ctx) return;
      const AC = global.AudioContext || global.webkitAudioContext;
      const ctx = (this.ctx = new AC());
      this.master = ctx.createGain();
      this.master.gain.value = 0.8;
      this.analyser = ctx.createAnalyser();
      this.analyser.fftSize = 2048;
      this.master.connect(this.analyser);
      this.analyser.connect(ctx.destination);
      this.musicBus = ctx.createGain();
      this.musicBus.connect(this.master);
      this.sfxBus = ctx.createGain();
      this.sfxBus.connect(this.master);
      this.waves = {};
      for (const d of [0.125, 0.25, 0.5, 0.75]) this.waves[d] = pulseWave(ctx, d);
      const buf = ctx.createBuffer(1, ctx.sampleRate, ctx.sampleRate);
      const data = buf.getChannelData(0);
      for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
      this.noiseBuffer = buf;
    }

    /** Call from a click or key handler before the first sound. */
    unlock() {
      this._init();
      return this.ctx.state === 'suspended' ? this.ctx.resume() : Promise.resolve();
    }

    /** Subscribe to playback steps: fn({ step, time, song }). Returns an unsubscribe function. */
    onStep(fn) {
      this.listeners.add(fn);
      return () => this.listeners.delete(fn);
    }

    play(song) {
      this._init();
      this.unlock();
      this.stop();
      this.song = compile(song);
      const ctx = this.ctx;
      this.playBus = ctx.createGain();
      this.playBus.connect(this.musicBus);
      this.chan = {};
      for (const ch of CHANNELS) {
        const g = ctx.createGain();
        g.gain.value = this.muted[ch] ? 0 : 1;
        g.connect(this.playBus);
        this.chan[ch] = g;
      }
      this.step = 0;
      this.nextTime = ctx.currentTime + 0.06;
      this.playing = true;
      this._timer = setInterval(() => this._tick(), 25);
      this._tick();
    }

    stop() {
      if (!this.playing) return;
      this.playing = false;
      clearInterval(this._timer);
      const bus = this.playBus;
      const t = this.ctx.currentTime;
      bus.gain.setValueAtTime(bus.gain.value, t);
      bus.gain.linearRampToValueAtTime(0, t + 0.05);
      setTimeout(() => bus.disconnect(), 300);
    }

    setMuted(ch, muted) {
      this.muted[ch] = muted;
      if (this.chan && this.chan[ch]) this.chan[ch].gain.setTargetAtTime(muted ? 0 : 1, this.ctx.currentTime, 0.01);
    }

    setVolume(v) {
      this._init();
      this.master.gain.setTargetAtTime(v, this.ctx.currentTime, 0.02);
    }

    sfx(name) {
      this._init();
      this.unlock();
      const fx = ChipTune.sfx[name];
      if (fx) fx(this, this.ctx.currentTime + 0.005);
    }

    // Look-ahead scheduler: queue notes slightly ahead of the audio clock so timers can jitter.
    _tick() {
      const stepDur = 60 / this.song.bpm / 4;
      while (this.nextTime < this.ctx.currentTime + 0.12) {
        this._scheduleStep(this.step, this.nextTime, stepDur);
        this.nextTime += stepDur;
        this.step = (this.step + 1) % this.song.length;
      }
    }

    _scheduleStep(step, t, stepDur) {
      const def = this.song.def;
      for (const ev of this.song.byStep[step]) {
        const cfg = def[ev.ch];
        if (ev.ch === 'noise') {
          this._drum(this.chan.noise, t, ev.note, cfg.vol);
        } else {
          const gate = cfg.gate || 0.9;
          this._tone(this.chan[ev.ch], t, ev.len * stepDur * gate, ev.freq, {
            wave: ev.ch === 'triangle' ? 'triangle' : 'pulse',
            duty: cfg.duty,
            vol: cfg.vol,
            sustain: cfg.sustain,
          });
        }
      }
      for (const fn of this.listeners) fn({ step, time: t, song: this.song });
    }

    _tone(bus, t, dur, freq, opts) {
      const ctx = this.ctx;
      const osc = ctx.createOscillator();
      const g = ctx.createGain();
      if (opts.wave === 'triangle') osc.type = 'triangle';
      else osc.setPeriodicWave(this.waves[opts.duty || 0.5]);
      osc.frequency.setValueAtTime(freq, t);
      if (opts.slideTo) osc.frequency.exponentialRampToValueAtTime(opts.slideTo, t + dur);
      const v = opts.vol;
      const sus = opts.sustain == null ? 0.6 : opts.sustain;
      const end = t + dur;
      const attack = t + 0.003;
      const decay = t + Math.min(0.09, dur * 0.5);
      const release = Math.max(decay + 0.001, end - 0.015);
      g.gain.setValueAtTime(0, t);
      g.gain.linearRampToValueAtTime(v, attack);
      g.gain.linearRampToValueAtTime(v * sus, decay);
      g.gain.setValueAtTime(v * sus, release);
      g.gain.linearRampToValueAtTime(0, end);
      osc.connect(g).connect(bus);
      osc.start(t);
      osc.stop(end + 0.02);
    }

    _noise(bus, t, dur, vol, filter) {
      const ctx = this.ctx;
      const src = ctx.createBufferSource();
      src.buffer = this.noiseBuffer;
      const f = ctx.createBiquadFilter();
      f.type = filter.type;
      f.frequency.setValueAtTime(filter.freq, t);
      if (filter.freqTo) f.frequency.exponentialRampToValueAtTime(filter.freqTo, t + dur);
      f.Q.value = filter.Q || 0.7;
      const g = ctx.createGain();
      g.gain.setValueAtTime(vol, t);
      g.gain.exponentialRampToValueAtTime(0.001, t + dur);
      src.connect(f).connect(g).connect(bus);
      src.start(t, Math.random() * 0.5, dur + 0.02);
    }

    _drum(bus, t, kind, vol) {
      const ctx = this.ctx;
      if (kind === 'k') {
        const osc = ctx.createOscillator();
        const g = ctx.createGain();
        osc.type = 'triangle';
        osc.frequency.setValueAtTime(170, t);
        osc.frequency.exponentialRampToValueAtTime(42, t + 0.12);
        g.gain.setValueAtTime(vol * 1.6, t);
        g.gain.exponentialRampToValueAtTime(0.001, t + 0.16);
        osc.connect(g).connect(bus);
        osc.start(t);
        osc.stop(t + 0.18);
      } else if (kind === 's') {
        this._noise(bus, t, 0.16, vol * 0.9, { type: 'bandpass', freq: 1900, Q: 0.8 });
        this._tone(bus, t, 0.07, 190, { wave: 'triangle', vol: vol * 0.8, sustain: 0.3 });
      } else if (kind === 'h') {
        this._noise(bus, t, 0.035, vol * 0.45, { type: 'highpass', freq: 7000 });
      } else if (kind === 'o') {
        this._noise(bus, t, 0.2, vol * 0.35, { type: 'highpass', freq: 6000 });
      } else if (kind === 'c') {
        this._noise(bus, t, 0.9, vol * 0.4, { type: 'highpass', freq: 3500 });
      }
    }
  }

  // ---- Sound effects -------------------------------------------------------------------

  const f = (note) => midiToFreq(noteToMidi(note));

  ChipTune.sfx = {
    coin(c, t) {
      c._tone(c.sfxBus, t, 0.07, f('B5'), { duty: 0.5, vol: 0.16, sustain: 1 });
      c._tone(c.sfxBus, t + 0.07, 0.3, f('E6'), { duty: 0.5, vol: 0.16, sustain: 0.5 });
    },
    jump(c, t) {
      c._tone(c.sfxBus, t, 0.17, 170, { duty: 0.25, vol: 0.16, slideTo: 640, sustain: 0.8 });
    },
    laser(c, t) {
      c._tone(c.sfxBus, t, 0.2, 1500, { duty: 0.125, vol: 0.14, slideTo: 160, sustain: 0.7 });
    },
    hit(c, t) {
      c._noise(c.sfxBus, t, 0.12, 0.35, { type: 'lowpass', freq: 3000, freqTo: 400 });
      c._tone(c.sfxBus, t, 0.14, 260, { duty: 0.5, vol: 0.14, slideTo: 60, sustain: 0.6 });
    },
    explosion(c, t) {
      c._noise(c.sfxBus, t, 0.9, 0.6, { type: 'lowpass', freq: 2400, freqTo: 80 });
      c._tone(c.sfxBus, t, 0.35, 110, { wave: 'triangle', vol: 0.4, slideTo: 30, sustain: 0.5 });
    },
    powerup(c, t) {
      ['C5', 'E5', 'G5', 'C6', 'D5', 'F#5', 'A5', 'D6', 'E5', 'G#5', 'B5', 'E6'].forEach((n, i) =>
        c._tone(c.sfxBus, t + i * 0.042, 0.05, f(n), { duty: 0.25, vol: 0.13, sustain: 1 }));
    },
    select(c, t) {
      c._tone(c.sfxBus, t, 0.05, f('A5'), { duty: 0.125, vol: 0.12, sustain: 1 });
    },
    levelup(c, t) {
      [['G5', 0.1], ['C6', 0.1], ['E6', 0.1], ['G6', 0.45]].reduce((at, [n, d]) => {
        c._tone(c.sfxBus, at, d, f(n), { duty: 0.25, vol: 0.15, sustain: 0.7 });
        c._tone(c.sfxBus, at, d, f(n) / 2, { wave: 'triangle', vol: 0.3, sustain: 0.8 });
        return at + d;
      }, t);
    },
  };

  // ---- Songs ---------------------------------------------------------------------------

  // Arpeggiate one chord per bar through a pattern of chord-tone indexes.
  function arp(chords, pattern, stepLen) {
    const len = stepLen || 1;
    return chords.map((chord) =>
      Array.from({ length: 16 / len }, (_, i) => chord[pattern[i % pattern.length]] + ':' + len).join(' ')
    ).join(' ');
  }

  // One bar of bass per root, from a rhythm of "semitoneOffset:steps" tokens that fills 16 steps.
  function bass(roots, rhythm) {
    return roots.map((root) =>
      rhythm.split(' ').map((tok) => {
        const [off, len] = tok.split(':');
        return transpose(root, Number(off)) + ':' + len;
      }).join(' ')
    ).join(' ');
  }

  const C = ['C4', 'E4', 'G4'], Am = ['A3', 'C4', 'E4'], F = ['F3', 'A3', 'C4'], G = ['G3', 'B3', 'D4'];

  ChipTune.songs = {
    overworld: {
      name: 'Overworld',
      bpm: 150,
      pulse1: {
        duty: 0.25, vol: 0.15,
        line: [
          'E5:2 G5:2 C6:4 B5:2 G5:2 E5:4',
          'A5:2 C6:2 E6:4 D6:2 C6:2 A5:4',
          'F5:2 A5:2 C6:3 A5:1 G5:2 F5:2 E5:2 D5:2',
          'D5:2 G5:2 B5:2 D6:2 C6:4 B5:4',
          'E5:2 G5:2 C6:4 B5:2 G5:2 E5:4',
          'A5:2 C6:2 E6:4 D6:2 C6:2 A5:4',
          'F5:2 A5:2 C6:2 F6:2 E6:2 C6:2 A5:2 C6:2',
          'B5:2 D6:2 G5:2 B5:2 C6:8',
        ].join(' '),
      },
      pulse2: { duty: 0.125, vol: 0.06, sustain: 0.8, line: arp([C, Am, F, G], [0, 1, 2, 1]) },
      triangle: { vol: 0.32, gate: 0.8, line: bass(['C3', 'A2', 'F2', 'G2'], '0:2 12:2 7:2 12:2 0:2 12:2 7:2 12:2') },
      noise: { vol: 0.35, line: 'k.h.s.h.k.khs.hh' },
    },

    dungeon: {
      name: 'Dungeon',
      bpm: 112,
      pulse1: {
        duty: 0.5, vol: 0.12, sustain: 0.5,
        line: [
          'A4:3 C5:1 E5:4 D5:2 C5:2 B4:4',
          'C5:3 A4:1 F4:4 A4:2 C5:2 F5:4',
          'D5:3 F5:1 A5:4 G5:2 F5:2 E5:2 D5:2',
          'E5:2 B4:2 G#4:4 E4:4 r:4',
        ].join(' '),
      },
      // The classic echo trick: the second pulse replays the lead three steps late, quieter.
      pulse2: {
        duty: 0.25, vol: 0.045, sustain: 0.5, delay: 3,
        line: [
          'A4:3 C5:1 E5:4 D5:2 C5:2 B4:4',
          'C5:3 A4:1 F4:4 A4:2 C5:2 F5:4',
          'D5:3 F5:1 A5:4 G5:2 F5:2 E5:2 D5:2',
          'E5:2 B4:2 G#4:4 E4:4 r:4',
        ].join(' '),
      },
      triangle: { vol: 0.34, gate: 0.85, line: bass(['A2', 'F2', 'D2', 'E2'], '0:3 0:1 12:2 0:2 7:4 0:4') },
      noise: { vol: 0.3, line: 'k...h...s.k.h...' },
    },

    boss: {
      name: 'Boss Fight',
      bpm: 172,
      pulse1: {
        duty: 0.25, vol: 0.14,
        line: [
          'E5:1 E5:1 r:1 E5:1 G5:2 E5:2 B5:4 A5:2 G5:2',
          'G5:1 G5:1 r:1 G5:1 E5:2 G5:2 C6:4 B5:2 A5:2',
          'F#5:1 F#5:1 r:1 F#5:1 A5:2 D6:2 C6:2 B5:2 A5:2 F#5:2',
          'D#5:2 F#5:2 B5:2 A5:2 G5:2 F#5:2 D#5:4',
        ].join(' '),
      },
      pulse2: {
        duty: 0.125, vol: 0.055, sustain: 0.8,
        line: arp([['E4', 'G4', 'B4'], ['C4', 'E4', 'G4'], ['D4', 'F#4', 'A4'], ['B3', 'D#4', 'F#4']], [0, 2, 1, 2]),
      },
      triangle: { vol: 0.34, gate: 0.7, line: bass(['E2', 'C2', 'D2', 'B1'], '0:1 12:1 0:1 12:1 0:1 12:1 0:1 12:1 0:1 12:1 0:1 12:1 0:1 12:1 0:1 12:1') },
      noise: { vol: 0.35, line: 'k.hhs.hkk.hhs.hs' },
    },
  };

  ChipTune.CHANNELS = CHANNELS;
  ChipTune.compile = compile;
  ChipTune.helpers = { arp, bass, transpose, noteToMidi, midiToNote };
  global.ChipTune = ChipTune;
})(typeof window !== 'undefined' ? window : globalThis);
