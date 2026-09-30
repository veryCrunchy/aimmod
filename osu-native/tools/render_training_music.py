"""Compose original AimMod training music. Requires numpy and ffmpeg on PATH."""
import argparse
import math
from pathlib import Path
import subprocess
import tempfile
import wave
import numpy as np

RATE = 44100


def write_wave(path, data):
    with wave.open(str(path), 'wb') as f:
        f.setnchannels(1 if data.ndim == 1 else 2)
        f.setsampwidth(2)
        f.setframerate(RATE)
        f.writeframes((np.clip(data, -.98, .98) * 32767).astype('<i2').tobytes())


def tone(midi, seconds, attack=.006, decay=4, colour=.15):
    t = np.arange(int(seconds * RATE), dtype=np.float32) / RATE
    frequency = 440 * 2 ** ((midi - 69) / 12)
    y = np.sin(2*np.pi*frequency*t + colour*np.sin(2*np.pi*frequency*2*t)*np.exp(-t*9))
    y += colour*np.sin(2*np.pi*frequency*2*t) + colour*.2*np.sin(2*np.pi*frequency*3*t)
    return y * np.minimum(t/attack, 1) * np.exp(-t*decay) * np.clip((seconds-t)/.03, 0, 1)


def cue(style):
    t = np.arange(int(.16*RATE), dtype=np.float32)/RATE
    rng = np.random.default_rng(614)
    if style == 'pulse':
        y = tone(62, .16, .001, 32, .3) + .3*tone(50, .16, .001, 35)
    elif style == 'glass':
        y = tone(74, .16, .001, 28, .07) + .2*tone(81, .16, .001, 40)
    else:
        noise = rng.normal(0, 1, len(t)).astype(np.float32)
        noise = np.convolve(noise, np.ones(5)/5, mode='same')
        y = tone(62, .16, .001, 45, .1) + .55*noise*np.exp(-t*85)*np.minimum(t/.001, 1)
    return (y * .85/max(abs(y).max(), 1e-5)).astype(np.float32)


def compose(bpm, flavour, seconds=184):
    n = int(seconds*RATE)
    music = np.zeros((n, 2), np.float32)
    drums = np.zeros_like(music)
    rng = np.random.default_rng(615+flavour)
    beat = 60/bpm
    start = .5+4*beat

    def add(bus, at, sound, gain=1, pan=0):
        k = int(round(at*RATE)); count = min(len(sound), n-k)
        if k < 0 or count <= 0: return
        bus[k:k+count, 0] += sound[:count]*gain*math.sqrt((1-pan)/2)
        bus[k:k+count, 1] += sound[:count]*gain*math.sqrt((1+pan)/2)

    t = np.arange(int(.32*RATE), dtype=np.float32)/RATE
    kick = np.sin(2*np.pi*(47*t + 75*(1-np.exp(-t*35))/35))*np.exp(-t*17)
    t = np.arange(int(.17*RATE), dtype=np.float32)/RATE
    noise = rng.normal(0, 1, len(t)).astype(np.float32)
    snare = (noise-np.convolve(noise, np.ones(15)/15, 'same'))*np.exp(-t*35)*.25 + np.sin(2*np.pi*180*t)*np.exp(-t*40)*.4
    t = np.arange(int(.05*RATE), dtype=np.float32)/RATE
    hat = rng.normal(0, 1, len(t)).astype(np.float32)*np.exp(-t*95)*np.minimum(t/.0008, 1)
    chords = [[50,57,60,64], [46,53,57,60], [53,60,64,69], [48,55,62,64]]
    hook = [74, 69, 72, 77, 74, 81, 77, 69]
    if flavour == 3:  # soft mallets, suspended chords and an ascending response
        chords = [[50,57,62,65], [48,55,60,64], [46,53,58,62], [45,52,57,60]]
        hook = [69,74,77,81,79,77,74,72]
    elif flavour == 4:  # syncopated neon arpeggio with a brighter upper voice
        chords = [[50,57,60,65], [53,60,64,69], [48,55,62,67], [46,53,57,62]]
        hook = [74,77,81,77,72,76,79,76]
    elif flavour == 5:  # lower, spacious melody and a gently resolving bass line
        chords = [[46,53,57,62], [50,57,60,65], [48,55,60,64], [45,52,57,64]]
        hook = [65,69,74,72,69,65,64,69]
    for i in range(4): add(drums, .5+i*beat, cue('pulse'), .6 if i<3 else .8)
    bars = int(math.ceil((seconds-start)/(4*beat)))
    for bar in range(bars):
        at = start+bar*4*beat
        section = (bar//8)%8
        chord = chords[(bar//2+flavour)%4]
        energy = .65 if section in (0,4,7) else 1
        for j, note in enumerate(chord):
            add(music, at, tone(note+12, beat*4.5, .12, .65/beat, .07), .12, (j-1.5)*.3)
        for b in range(4):
            add(drums, at+b*beat, kick, .8*energy if flavour not in (2,5) or b in (0,2) else .35)
            if b%2: add(drums, at+b*beat, snare, .7*energy)
            add(music, at+b*beat+(.5*beat if flavour in (1,4) and b%2 else 0), tone(chord[0]-12, beat*.65, .004, 5/beat, .2), .65*energy)
        for h in range(16 if flavour in (2,4) else 8):
            add(drums, at+h*beat*(.25 if flavour in (2,4) else .5), hat, (.1 if h%2 else .18)*energy, .25*(-1)**h)
        if section in (1,2,3,5,6):
            for step in range(8):
                if (step+bar)%3==0 and section in (1,5): continue
                note = hook[(step+bar%2*2+flavour)%8]
                voice = tone(note, beat*(1.4 if flavour in (3,5) else .9), .007, (3 if flavour in (3,5) else 5)/beat, .05 if flavour==3 else .13)
                pos = at+step*beat*.5
                pan = -.28 if step%2 else .28
                add(music, pos, voice, .21, pan)
                add(music, pos+beat*.75, voice, .045, -pan)
        if bar%8==7:
            for h in range(3): add(drums, at+3.25*beat+h*beat*.25, cue('snap'), .15+h*.025, (h-1)*.35)
    phase = ((np.arange(n, dtype=np.float32)/RATE-start)%beat)
    music *= (1-.42*np.exp(-phase/(beat*.15)))[:,None]
    data = np.tanh((music+drums)*1.1)
    fade = np.minimum(np.arange(n)/220, 1)*np.clip((n-np.arange(n))/(RATE*2.0),0,1)
    return data*fade[:,None]


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--previews',type=Path,required=True)
    parser.add_argument('--variants',action='store_true')
    args=parser.parse_args()
    args.output.mkdir(parents=True,exist_ok=True); args.previews.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='aimmod-music-') as temp:
        temp=Path(temp)
        for name in ('pulse','glass','snap'):
            sound=cue(name); write_wave(args.output/f'trainer-cue-{name}.wav',sound)
            audition=np.zeros(RATE*5,np.float32)
            for i, at in enumerate([.2,.7,1.2,1.7,2.3,2.425,2.55,3.3,3.425,3.55]):
                k=int(at*RATE); audition[k:k+len(sound)]+=sound*(.8 if i%3 else 1)
            write_wave(args.previews/f'aimmod-{name}-cue.wav',audition)
        catalog = [('midnight-pulse',120,0),('mint-current',150,1),('afterglow',180,2),
                   ('moonlit-orbit',120,3),('neon-cascade',150,4),('velvet-horizon',120,5)]
        jobs = [(name,bpm,flavour) for name,native,flavour in catalog for bpm in ([90,120,150,180,210] if args.variants else [native])]
        for name,bpm,flavour in jobs:
            target=args.output/f'training-{name}-{bpm}.ogg'
            if not target.exists():
                source=temp/f'{name}.wav'; write_wave(source,compose(bpm,flavour,185))
                subprocess.run(['ffmpeg','-v','error','-y','-i',str(source),'-af','loudnorm=I=-15:TP=-2.5:LRA=7','-ar',str(RATE),'-c:a','libvorbis','-q:a','4',str(target)],check=True)
            if bpm == dict((n,b) for n,b,f in catalog)[name]:
                subprocess.run(['ffmpeg','-v','error','-y','-i',str(target),'-c:a','libmp3lame','-b:a','192k',str(args.previews/f'{name}-{bpm}bpm.mp3')],check=True)
            print(f'{name}: {bpm} BPM, 185 seconds',flush=True)

if __name__=='__main__': main()
