"""Four distinct original arrangements sharing AimMod's short mint chime motif."""
import argparse
import math
from pathlib import Path
import subprocess
import tempfile
import numpy as np
from render_training_music import RATE, tone, cue, write_wave

SONGS = [('mint-breaker', 180, 'breakbeat'), ('night-drive', 120, 'synthwave'),
         ('sidechain-city', 150, 'garage'), ('tidal-signal', 90, 'percussion')]

def compose(bpm, style, seconds=185):
    n = int(seconds * RATE)
    data = np.zeros((n, 2), np.float32)
    rng = np.random.default_rng(811 + [s[2] for s in SONGS].index(style))
    beat = 60 / bpm
    start = .5 + 4 * beat

    def add(at, sound, gain=.3, pan=0):
        k = round(at * RATE)
        length = min(len(sound), n-k)
        if k < 0 or length <= 0: return
        data[k:k+length, 0] += sound[:length]*gain*math.sqrt((1-pan)/2)
        data[k:k+length, 1] += sound[:length]*gain*math.sqrt((1+pan)/2)

    def voice(midi, duration, kind):
        t = np.arange(int(duration*RATE), dtype=np.float32)/RATE
        f = 440*2**((midi-69)/12)
        if kind == 'analog':
            y = sum(np.sin(2*np.pi*f*(1+detune)*h*t)/h**1.6 for h in range(1,7) for detune in [-.003,.003])*.4
            envelope = np.minimum(t/.025,1)*np.exp(-t/(duration*.65))
        elif kind == 'reese':
            y = np.sin(2*np.pi*f*t + 1.4*np.sin(2*np.pi*f*.5*t)) + .2*np.sin(2*np.pi*(f+1.8)*t)
            envelope = np.minimum(t/.009,1)*np.exp(-t/(duration*.7))
        elif kind == 'marimba':
            y = np.sin(2*np.pi*f*t) + .25*np.sin(2*np.pi*f*4*t)*np.exp(-t*18)
            envelope = np.minimum(t/.003,1)*np.exp(-t*7/max(beat,.3))
        else:
            y = np.sin(2*np.pi*f*t + 2.4*np.sin(2*np.pi*f*2*t)*np.exp(-t*8))
            envelope = np.minimum(t/.002,1)*np.exp(-t*5/beat)
        return y*envelope*np.clip((duration-t)/.035,0,1)

    t = np.arange(int(.36*RATE),dtype=np.float32)/RATE
    kick = np.sin(2*np.pi*(43*t+95*(1-np.exp(-t*40))/40))*np.exp(-t*15)
    t = np.arange(int(.18*RATE),dtype=np.float32)/RATE
    noise = rng.normal(0,1,len(t)).astype(np.float32)
    snare = (noise-np.convolve(noise,np.ones(11)/11,'same'))*np.exp(-t*28)*.4 + np.sin(2*np.pi*190*t)*np.exp(-t*33)*.3
    t = np.arange(int(.06*RATE),dtype=np.float32)/RATE
    hat = rng.normal(0,1,len(t)).astype(np.float32)*np.exp(-t*85)*np.minimum(t/.001,1)
    roots = {'breakbeat':[38,41,36,43], 'synthwave':[45,41,48,43], 'garage':[38,43,41,36], 'percussion':[50,48,46,45]}[style]
    for i in range(4): add(.5+i*beat,cue('pulse'),.55 if i<3 else .75)
    bars = math.ceil((seconds-start)/(4*beat))
    for bar in range(bars):
        at = start+bar*4*beat
        section = (bar//8)%6
        # Intro, first groove, answer, open bridge, full return, stripped ending.
        sparse = section in (0,3,5)
        root = roots[(bar//2)%4]
        if style == 'breakbeat':
            kicks = [0,1.75,2.5] if bar%2==0 else [0,.75,2.75,3.5]
            snares = [1,3]
            bass = [(0,root),(.75,root),(2,root+7),(2.75,root-2)]
            if section==3: kicks,snares,bass = [0],[3],[(0,root)]
            for b,note in bass: add(at+b*beat,voice(note,beat*.7,'reese'),.43)
            if not sparse:
                for j in range(8):
                    add(at+j*.5*beat,voice(root+36+[0,7,10,14,7,3,10,5][(j+bar)%8],beat*.7,'fm'),.14,(-1)**j*.55)
            hats = [(h*.25,.11 if h%2 else .2) for h in range(16)]
        elif style == 'synthwave':
            kicks,snares = [0,2],[1,3]
            hats = [(h*.5,.12) for h in range(8)]
            for j in range(8): add(at+j*.5*beat,voice(root+(12 if j%4==3 else 0),beat*.42,'analog'),.32)
            for j,interval in enumerate([0,7,10,14]):
                add(at,voice(root+12+interval,beat*3.7,'analog'),.12,(j-1.5)*.4)
            if not sparse:
                for j,interval in enumerate([19,22,24,26] if bar%2 else [24,19,17,14]):
                    add(at+j*beat,voice(root+interval,beat*.85,'analog'),.20,(-1)**j*.25)
        elif style == 'garage':
            kicks,snares = ([0,1.5,2.75] if bar%2 else [0,2.5]),[1,3]
            hats = [(h*.5+(.12 if h%2 else 0),.14 if h%2 else .08) for h in range(8)]
            for b in [0,.75,2.5,3.25]: add(at+b*beat,voice(root,beat*.45,'reese'),.46)
            for b in ([.65,1.5,2.65,3.5] if not sparse else [1.5,3.5]):
                for j,interval in enumerate([12,15,19,22]): add(at+b*beat,voice(root+interval,beat*.4,'fm'),.14,(j-1.5)*.3)
        else:
            kicks,snares = [0,2],[]
            hats = [(h*.5,.08) for h in range(8)]
            for j in range(8 if not sparse else 4):
                b = j*(.5 if not sparse else 1)
                note = root+12+[0,7,12,3,10,7,5,2][(j+bar)%8]
                sound = voice(note,beat*1.8,'marimba')
                add(at+b*beat,sound,.45,math.sin(j)*.6)
                add(at+(b+.75)*beat,sound,.10,-math.sin(j)*.6)
            for b in [.75,1.5,2.75,3.25]: add(at+b*beat,voice(root-12,beat*.25,'fm'),.26)
            if section in (2,4):
                for j,interval in enumerate([0,7,14]): add(at,voice(root+interval,beat*3.7,'analog'),.11,(j-1)*.6)
        for b in kicks: add(at+b*beat,kick,.72 if style!='percussion' else .4)
        for b in snares: add(at+b*beat,snare,.62)
        for b,gain in hats: add(at+b*beat,hat,gain*(.6 if sparse else 1),(-1)**int(b*4)*.4)
        # Shared sonic signature returns as a phrase, not an identical lead loop.
        if bar%8==6:
            for j,note in enumerate([74,69,72,77]):
                chime=tone(note,beat*1.5,.004,4/beat,.08)
                add(at+j*.75*beat,chime,.24,(-1)**j*.4)
                add(at+(j*.75+.5)*beat,chime,.065,(-1)**(j+1)*.5)
        if bar%8==7:
            for j in range(4): add(at+(3+j*.25)*beat,snare if style=='breakbeat' else cue('snap'),.10+j*.035,(-1)**j*.3)
    fade=np.minimum(np.arange(n)/220,1)*np.clip((n-np.arange(n))/(RATE*2),0,1)
    return np.tanh(data*1.1)*fade[:,None]

def main():
    p=argparse.ArgumentParser()
    p.add_argument('--output',type=Path,required=True); p.add_argument('--previews',type=Path,required=True)
    args=p.parse_args(); args.output.mkdir(parents=True,exist_ok=True); args.previews.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='aimmod-arrangements-') as temp:
        for name,native,style in SONGS:
            for bpm in [90,120,150,180,210]:
                target=args.output/f'training-{name}-{bpm}.ogg'
                if not target.exists():
                    source=Path(temp)/'track.wav'; write_wave(source,compose(bpm,style))
                    subprocess.run(['ffmpeg','-v','error','-y','-i',str(source),'-af','loudnorm=I=-15:TP=-2.5:LRA=7','-ar',str(RATE),'-c:a','libvorbis','-q:a','4',str(target)],check=True)
                if bpm==native:
                    subprocess.run(['ffmpeg','-v','error','-y','-ss','15','-i',str(target),'-t','40','-c:a','libmp3lame','-b:a','192k',str(args.previews/f'{name}.mp3')],check=True)
                print(f'{name}: {bpm} BPM complete',flush=True)
if __name__=='__main__': main()
