/*
 * songs-adventure.js — longer, atmospheric songs for RPG and medieval games.
 * Load after chiptune.js; the songs join ChipTune.songs. All are original compositions.
 *
 *   <script src="chiptune.js"></script>
 *   <script src="songs-adventure.js"></script>
 *   chip.play(ChipTune.songs.longRoad);
 */
(function (global) {
  'use strict';

  const ChipTune = global.ChipTune;
  if (!ChipTune) throw new Error('Load chiptune.js before songs-adventure.js');
  const { arp, bass } = ChipTune.helpers;

  // Each melody is written as a list of bars so every bar can be checked for length.
  const line = (bars) => bars.join(' ');
  const repeat = (text, times) => Array(times).fill(text).join(' ');

  // ---- The Long Road: an RPG field theme. D major, 4/4, 32 bars in four 8-bar sections. ----

  const D = ['D4', 'F#4', 'A4'], A = ['C#4', 'E4', 'A4'], Bm = ['B3', 'D4', 'F#4'];
  const Fsm = ['C#4', 'F#4', 'A4'], G = ['B3', 'D4', 'G4'], Em = ['B3', 'E4', 'G4'];

  const roadChords = [
    D, A, Bm, Fsm, G, D, Em, A, // A: the theme
    D, A, Bm, Fsm, G, D, Em, D, // A': the theme answered, resolving home
    Bm, G, D, A, Bm, G, Em, A, // B: the minor turn
    G, A, Fsm, Bm, Em, A, D, A, // C: the climb, turning back to the top
  ];
  const roadRoots = [
    'D3', 'A2', 'B2', 'F#2', 'G2', 'D3', 'E2', 'A2',
    'D3', 'A2', 'B2', 'F#2', 'G2', 'D3', 'E2', 'D3',
    'B2', 'G2', 'D3', 'A2', 'B2', 'G2', 'E2', 'A2',
    'G2', 'A2', 'F#2', 'B2', 'E2', 'A2', 'D3', 'A2',
  ];
  const roadMelody = [
    'F#5:6 E5:2 D5:4 A4:4', 'C#5:6 D5:2 E5:8', 'D5:6 C#5:2 B4:4 F#4:4', 'A4:6 B4:2 C#5:8',
    'B4:6 C#5:2 D5:4 G5:4', 'F#5:6 E5:2 D5:4 F#5:4', 'G5:4 F#5:4 E5:4 D5:4', 'E5:12 r:4',

    'F#5:6 E5:2 D5:4 A4:4', 'C#5:6 D5:2 E5:4 A5:4', 'B5:6 A5:2 F#5:4 D5:4', 'C#5:6 D5:2 E5:4 F#5:4',
    'G5:6 F#5:2 E5:4 D5:4', 'A5:6 F#5:2 D5:8', 'E5:4 G5:4 F#5:4 E5:4', 'D5:12 r:4',

    'B4:4 D5:4 F#5:8', 'G5:4 F#5:4 E5:4 D5:4', 'F#5:4 A5:4 D6:8', 'C#6:4 B5:4 A5:8',
    'B5:6 A5:2 F#5:4 D5:4', 'E5:6 F#5:2 G5:4 B5:4', 'A5:4 G5:4 F#5:4 E5:4', 'E5:4 F#5:2 G5:2 A5:8',

    'B5:6 A5:2 G5:4 D6:4', 'C#6:6 B5:2 A5:8', 'A5:6 G5:2 F#5:4 C#5:4', 'D5:4 F#5:4 B5:8',
    'G5:6 F#5:2 E5:4 B4:4', 'C#5:4 E5:4 A5:8', 'F#5:6 E5:2 D5:4 A4:4', 'C#5:4 D5:2 E5:2 A4:8',
  ];

  // ---- Village Square: a medieval dance. D Dorian, 6/8, 32 bars over a drone bass. ----

  const Dm = ['D4', 'F4', 'A4'], C = ['C4', 'E4', 'G4'], Gmaj = ['B3', 'D4', 'G4'];
  const Am = ['A3', 'C4', 'E4'], F = ['A3', 'C4', 'F4'];

  const villageChords = [
    Dm, Dm, C, C, Dm, Gmaj, Am, Dm,
    Dm, C, Dm, Gmaj, Dm, C, Am, Dm,
    F, C, F, Gmaj, Dm, C, Am, Dm,
    Am, Dm, C, F, Dm, Am, Gmaj, Dm, // a quieter interlude before the dance starts again
  ];
  const villageRoots = [
    'D3', 'D3', 'C3', 'C3', 'D3', 'G2', 'A2', 'D3',
    'D3', 'C3', 'D3', 'G2', 'D3', 'C3', 'A2', 'D3',
    'F2', 'C3', 'F2', 'G2', 'D3', 'C3', 'A2', 'D3',
    'A2', 'D3', 'C3', 'F2', 'D3', 'A2', 'G2', 'D3',
  ];
  const villageMelody = [
    'D5:4 E5:2 F5:4 G5:2', 'A5:4 G5:2 F5:4 E5:2', 'E5:4 D5:2 C5:4 E5:2', 'G5:6 E5:6',
    'D5:4 E5:2 F5:4 G5:2', 'A5:4 B5:2 C6:4 B5:2', 'A5:4 G5:2 E5:4 C5:2', 'D5:12',

    'A5:2 G5:2 F5:2 E5:2 D5:4', 'E5:2 F5:2 G5:2 E5:2 C5:4', 'F5:2 G5:2 A5:2 F5:2 D5:4', 'B4:4 C5:2 D5:6',
    'A5:4 A5:2 A5:4 G5:2', 'G5:4 F5:2 E5:4 C5:2', 'E5:2 F5:2 G5:2 F5:2 E5:2 C5:2', 'D5:6 r:6',

    'C6:4 A5:2 F5:4 A5:2', 'G5:4 E5:2 C5:4 E5:2', 'F5:4 G5:2 A5:4 C6:2', 'D6:6 B5:6',
    'D6:4 C6:2 A5:4 F5:2', 'E5:4 G5:2 C6:4 G5:2', 'A5:4 G5:2 E5:4 C5:2', 'D5:12',

    'E5:4 D5:2 C5:4 A4:2', 'F5:4 E5:2 D5:4 A4:2', 'G4:4 A4:2 C5:4 E5:2', 'F5:6 A5:6',
    'A5:4 G5:2 F5:4 D5:2', 'E5:4 C5:2 A4:4 C5:2', 'B4:4 D5:2 G5:4 B4:2', 'D5:6 A4:6',
  ];

  // ---- Castle Hall: a stately processional. G minor, 3/4, 24 bars. ----

  const Gm = ['G3', 'Bb3', 'D4'], Cm = ['G3', 'C4', 'Eb4'], Dmaj = ['F#3', 'A3', 'D4'];
  const Eb = ['G3', 'Bb3', 'Eb4'], Bb = ['F3', 'Bb3', 'D4'], Fmaj = ['F3', 'A3', 'C4'];

  const hallChords = [
    Gm, Cm, Dmaj, Gm, Eb, Bb, Cm, Dmaj,
    Gm, Cm, Fmaj, Bb, Eb, Cm, Dmaj, Gm,
    Bb, Fmaj, Gm, Dmaj, Eb, Cm, Dmaj, Dmaj,
  ];
  const hallRoots = [
    'G2', 'C3', 'D3', 'G2', 'Eb2', 'Bb2', 'C3', 'D3',
    'G2', 'C3', 'F2', 'Bb2', 'Eb2', 'C3', 'D3', 'G2',
    'Bb2', 'F2', 'G2', 'D3', 'Eb2', 'C3', 'D3', 'D3',
  ];
  const hallMelody = [
    'G4:4 Bb4:6 A4:2', 'G4:4 C5:8', 'F#4:4 A4:6 G4:2', 'D5:12',
    'Eb5:4 D5:6 C5:2', 'D5:4 Bb4:8', 'C5:4 Eb5:4 G4:4', 'F#4:4 A4:4 D5:4',

    'G5:4 F5:6 Eb5:2', 'Eb5:4 D5:4 C5:4', 'C5:4 F5:6 Eb5:2', 'D5:12',
    'G5:4 F5:4 Eb5:4', 'Eb5:4 D5:4 C5:4', 'A4:4 D5:4 F#5:4', 'G5:12',

    'F5:4 D5:6 Bb4:2', 'C5:4 F5:8', 'Bb5:4 A5:4 G5:4', 'F#5:4 A5:8',
    'G5:4 Eb5:6 C5:2', 'Eb5:4 C5:4 G4:4', 'F#4:4 A4:4 C5:4', 'D5:8 r:4',
  ];

  // ---- Ancient Forest: slow and atmospheric. E minor, 4/4, 16 bars; harp first, then the lead. ----

  const EmH = ['E3', 'B3', 'E4', 'G4'], CH = ['C3', 'G3', 'E4', 'G4'], DH = ['D3', 'A3', 'D4', 'F#4'];
  const AmH = ['A2', 'E3', 'A3', 'C4'], BH = ['B2', 'F#3', 'B3', 'D#4'], GH = ['G2', 'D3', 'G3', 'B3'];

  const forestChords = [EmH, CH, DH, EmH, EmH, CH, AmH, BH, EmH, GH, DH, CH, AmH, CH, BH, EmH];
  const forestRoots = ['E2', 'C2', 'D2', 'E2', 'E2', 'C2', 'A2', 'B1', 'E2', 'G2', 'D2', 'C2', 'A2', 'C2', 'B1', 'E2'];
  const forestMelody = [
    'r:16', 'r:16', 'r:16', 'r:16',
    'B4:8 E5:4 F#5:4', 'G5:12 E5:4', 'A5:8 G5:4 E5:4', 'F#5:16',
    'B5:8 A5:4 G5:4', 'D5:8 G5:8', 'F#5:8 A5:4 F#5:4', 'E5:16',
    'C5:8 E5:4 A5:4', 'G5:8 E5:8', 'D#5:8 F#5:8', 'E5:16',
  ];

  const adventure = {
    longRoad: {
      name: 'The Long Road', group: 'Adventure', bpm: 100,
      pulse1: { duty: 0.5, vol: 0.13, sustain: 0.7, line: line(roadMelody) },
      pulse2: { duty: 0.25, vol: 0.05, sustain: 0.7, line: arp(roadChords, [0, 1, 2, 1], 2) },
      triangle: { vol: 0.3, gate: 0.85, line: bass(roadRoots, '0:4 7:4 12:4 7:4') },
      noise: { vol: 0.24, line: repeat('k...h...s...h...', 16) + ' ' + repeat('k.h.s.h.k.khs.h.', 15) + ' k.h.s.h.s.sss.c.' },
    },

    villageSquare: {
      name: 'Village Square', group: 'Adventure', bpm: 144, barSteps: 12, beatSteps: 6,
      pulse1: { duty: 0.5, vol: 0.13, sustain: 0.6, line: line(villageMelody) },
      pulse2: { duty: 0.125, vol: 0.05, sustain: 0.7, line: arp(villageChords, [0, 2, 1], 2, 12) },
      triangle: { vol: 0.3, gate: 0.95, line: bass(villageRoots, '0:6 7:6') },
      noise: { vol: 0.22, line: repeat('k...h.s...h.', 16) + ' ' + repeat('k.h.h.s.h.h.', 8) + ' ' + repeat('k...h.....h.', 8) },
    },

    castleHall: {
      name: 'Castle Hall', group: 'Adventure', bpm: 84, barSteps: 12,
      pulse1: { duty: 0.25, vol: 0.13, sustain: 0.75, line: line(hallMelody) },
      pulse2: { duty: 0.5, vol: 0.05, sustain: 0.85, line: arp(hallChords, [0, 2, 1], 4, 12) },
      triangle: { vol: 0.32, gate: 0.95, line: bass(hallRoots, '0:8 12:4') },
      noise: { vol: 0.24, line: repeat('k.......k...', 16) + ' ' + repeat('k...s...k.h.', 7) + ' k...s.s.sssc' },
    },

    ancientForest: {
      name: 'Ancient Forest', group: 'Adventure', bpm: 68,
      pulse1: { duty: 0.5, vol: 0.12, sustain: 0.8, line: line(forestMelody) },
      pulse2: { duty: 0.25, vol: 0.045, sustain: 0.5, gate: 0.95, line: arp(forestChords, [0, 1, 2, 3, 2, 1], 1) },
      triangle: { vol: 0.3, gate: 0.98, line: bass(forestRoots, '0:16') },
      noise: { vol: 0.12, line: '................ ........o.......' },
    },
  };

  Object.assign(ChipTune.songs, adventure);
})(typeof window !== 'undefined' ? window : globalThis);
