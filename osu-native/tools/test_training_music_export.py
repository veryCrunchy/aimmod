"""Numerical regression checks for rendered-percussion timing. Run with unittest."""
import tempfile
import unittest
from pathlib import Path

import numpy as np
import soundfile as sf

from export_studio_training_music import drum_attacks


class DrumTimingTests(unittest.TestCase):
    def test_swing_and_releases_do_not_become_extra_notes(self):
        rate = 48000
        audio = np.zeros((rate * 3, 2), np.float32)
        expected = [.5, .8, 1., 1.3, 1.5, 1.8]
        t = np.arange(int(rate * .18)) / rate
        transient = np.sin(t * 15000) * np.exp(-t * 40)
        for time in expected:
            start = round(time * rate)
            audio[start:start + len(t)] += transient[:, None] * .2
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'hats.flac'
            sf.write(path, audio, rate)
            attacks = drum_attacks(path, 120)
        self.assertEqual(len(attacks), len(expected))
        for actual, time in zip(attacks, expected):
            self.assertAlmostEqual(actual[0] * .5 + .5, time, delta=.003)

    def test_silence_does_not_supply_a_practice_rhythm(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'hats.flac'
            sf.write(path, np.zeros((48000, 2)), 48000)
            self.assertEqual(drum_attacks(path, 120), [])


if __name__ == '__main__':
    unittest.main()
