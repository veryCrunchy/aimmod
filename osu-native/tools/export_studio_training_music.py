"""Import the reviewed studio collection and its MIDI into tempo-matched trainer assets.

Usage: python export_studio_training_music.py --collection <studio-collection> --output <Resources>
Requires ffmpeg with rubberband, numpy, soundfile and mido. No analysis at app startup.
"""
import argparse, concurrent.futures, functools, hashlib, json, math, subprocess, tempfile
from pathlib import Path
import mido
import numpy as np
import soundfile as sf

RATE=48000
TEMPOS=[90,120,150,180,210]

@functools.lru_cache(maxsize=64)
def drum_attacks(path,bpm):
    """Read attacks from isolated rendered drums, which do not have MIDI exports.

    A 2 ms peak envelope and preceding 8 ms reject decay/reverb; a 60 ms
    refractory interval prevents one transient from producing several objects.
    Preserve the played swing instead of replacing it with straight subdivisions.
    """
    if not path.exists(): return []
    audio,rate=sf.read(path,dtype='float32',always_2d=True)
    hop=max(1,round(rate*.002))
    mono=np.max(np.abs(audio),axis=1)
    envelope=mono[:len(mono)//hop*hop].reshape(-1,hop).max(axis=1)
    previous=np.maximum.reduce([np.concatenate([np.zeros(i),envelope[:-i]]) for i in range(1,5)])
    flux=np.maximum(0,envelope-previous)
    if not len(flux) or flux.max()<1e-6:return []
    hits=[]
    for index in np.flatnonzero((flux>flux.max()*.08)&(envelope>envelope.max()*.035)):
        if not hits or index-hits[-1]>30: hits.append(int(index))
    return [((index*hop/rate-.5)*bpm/60,0,0,1,path.stem) for index in hits]

def midi_notes(path,bpm):
    clock=0;active={};result=[]
    for message in mido.MidiFile(path):
        clock+=message.time
        if message.type=='note_on' and message.velocity:
            active[message.note]=(clock,message.velocity)
        elif message.type in ['note_off','note_on'] and message.note in active:
            start,velocity=active.pop(message.note)
            result.append(((start-.5)*bpm/60,(clock-start)*bpm/60,message.note,velocity/127))
    return result

def verify_signature(session,meta,signature,native):
    bars=[0,next(p['time'] for p in meta['sections'] if p['title']=='Breakdown')]
    bars[1]=round((bars[1]-.5)*native/240)+2
    bars.extend([int(meta['bars'])-16,int(meta['bars'])-2])
    occurrences=meta.get('signature_occurrences') or [dict(bar=bar,
        role='keys' if quote in [0,1,3] else 'answer' if meta['index'] in [7,13] else 'lead',
        transpose=signature['quote_transpose']) for quote,bar in enumerate(bars)]
    for occurrence in occurrences:
        bar=occurrence['bar'];role=occurrence['role']
        notes=sorted(n for n in midi_notes(session/'MIDI'/f'{role}.mid',native)
                     if bar*4-.01<=n[0]<bar*4+8-.01 and n[3]>=70/127)
        expected=[n+occurrence['transpose'] for n in signature['melody_midi']]
        if [n[2] for n in notes]!=expected or abs(notes[-1][1]-3.75)>.01:
            raise ValueError('Incorrect three-note signature: '+meta['title'])

def export(collection,output,track,bpm,profiles_only=False):
    name=track['title'];slug=track['slug'];native=track['bpm'];beat=60/bpm;bar=4*beat
    session=collection/'Sessions'/name
    meta=json.loads((session/'arrangement.json').read_text())
    signature=json.loads((session/'correct-signature.json').read_text())
    if not meta.get('motifEmbeddedMidi') or signature.get('melody_midi')!=[64,65,69]:
        raise ValueError('Export requires the verified three-note startup melody in MIDI: '+name)
    verify_signature(session,meta,signature,native)
    source=collection/track['wav']
    source_blocks=int(meta['bars'])//8
    blocks=math.ceil(185/(bar*8))
    # Keep the opening and ending once. Repeat complete internal phrases at faster tempos.
    if blocks<=source_blocks:
        sequence=np.rint(np.linspace(0,source_blocks-1,blocks)).astype(int).tolist()
    else:
        middle=list(range(1,source_blocks-1));sequence=[0]
        while len(sequence)<blocks-1:
            sequence.extend(middle[:blocks-1-len(sequence)])
        sequence.append(source_blocks-1)
    target=output/'Audio'/f'training-{slug}-{bpm}.ogg'
    if not profiles_only:
        original,rate=sf.read(source,dtype='float32');assert rate==RATE
        with tempfile.TemporaryDirectory(prefix='studio-tempo-',dir=output.parent) as tmp:
            stretched=Path(tmp)/'stretched.wav'
            subprocess.run(['ffmpeg','-v','error','-y','-i',str(source),'-af',f'rubberband=tempo={bpm/native}','-ar',str(RATE),'-c:a','pcm_f32le',str(stretched)],check=True)
            y,sr=sf.read(stretched,dtype='float32');factor=native/bpm
            musical_outro=bool(meta.get('motifEmbeddedMidi'))
            music_end=.5+source_blocks*32*60/native
            tail=y[round(music_end*factor*RATE):] if musical_outro else original[round((float(meta['duration'])-3.65)*RATE):]
            count=round((.5+blocks*32*beat)*RATE)+len(tail)
            audio=np.zeros((count,2),np.float32)
            source_end=(music_end if musical_outro else float(meta['duration'])-3.65)*factor
            for dest,src in enumerate(sequence):
                begin=(.5+src*32*60/native)*factor
                finish=min((.5+(src+1)*32*60/native)*factor,source_end)
                a=round(begin*RATE);b=round(finish*RATE)
                segment=y[a:b].copy();dst=round((.5+dest*32*beat)*RATE)
                wanted=round((.5+(dest+1)*32*beat)*RATE)-dst
                segment=segment[:wanted]
                fade=min(240,len(segment)//2)
                segment[:fade]*=np.linspace(0,1,fade)[:,None]
                segment[-fade:]*=np.linspace(1,0,fade)[:,None]
                audio[dst:dst+len(segment)]=segment
            # Musical endings follow the new tempo too, including their natural release.
            dst=round((.5+blocks*32*beat)*RATE)
            audio[dst:dst+min(len(tail),count-dst)]=tail[:count-dst]
            # Time stretching can add peaks; reserve headroom for Vorbis reconstruction.
            if not np.isfinite(audio).all():raise ValueError('Non-finite audio: '+name)
            peak=float(np.max(np.abs(audio)))
            if peak>.78:audio*=.78/peak
            target=output/'Audio'/f'training-{slug}-{bpm}.ogg'
            sf.write(stretched,audio,RATE,subtype='PCM_24')
            subprocess.run(['ffmpeg','-v','error','-y','-i',str(stretched),'-c:a','libvorbis','-q:a','5',str(target)],check=True)

    events=[];holds=[];sections=[]
    source_notes=[]
    for role in ['hats','snare','percussion']:
        source_notes.extend(drum_attacks(session/'Stems'/f'{role}.flac',native))
    for role in ['bass','lead','arp','pad','keys','answer','texture','motif-keys','motif-lead','motif-answer']:
        if not (session/'MIDI'/f'{role}.mid').exists(): continue
        for onset,length,pitch,velocity in midi_notes(session/'MIDI'/f'{role}.mid',native):
            if role in ['lead','answer'] and any(e['bar']*4<=onset<e['bar']*4+6.2 for e in meta.get('signature_extra_occurrences',[])):
                continue # Follow the foreground theme while the previous melody is quietened.
            source_notes.append((onset,length,pitch,velocity,role))
    # The three-note signature is performed in the instrument MIDI like any other phrase.
    kick_path=session/'kick-times.json'
    if kick_path.exists():
        for time in json.loads(kick_path.read_text()):source_notes.append(((time-.5)*native/60,0,0,1,'kick'))
    else: # Original Night Drive session retained its original kick list separately.
        for b in range(int(meta['bars'])):
            source_notes.extend([(b*4,0,0,1,'kick'),(b*4+2,0,0,.9,'kick')])
    points=meta['sections']
    for dest,src in enumerate(sequence):
        first=src*32;last=first+32;shift=dest*32-first
        sec=next((p for p in reversed(points) if (p['time']-.5)*native/60<=first+.01),points[0])
        label=sec['title']
        energy=.35 if label in ['Intro','Breakdown','Outro'] else .7 if label in ['Groove','Build'] else 1
        sections.append(dict(StartBeat=dest*32,EndBeat=(dest+1)*32,Energy=energy,Quiet=energy<.5))
        for onset,length,pitch,velocity,role in source_notes:
            if not first<=onset<last:continue
            t=round(onset+shift,5)
            strength=1 if role.startswith('motif-') else .95 if role in ['lead','answer'] else .9 if role=='kick' else .85 if role=='snare' else .8 if role=='keys' else .75 if role=='bass' else .6 if role=='percussion' else .5
            if role not in ['pad','texture']:events.append(dict(Beat=t,Strength=strength,Pitch=pitch))
            # Long harmony and melodic holds are authored opportunities, not random objects.
            if length>=.55 and (role in ['lead','answer','pad','keys','texture'] or role.startswith('motif-')):
                holds.append(dict(Beat=t,Beats=round(min(length,last-onset),5),Quiet=energy<.5,Melody=role in ['lead','answer','keys'] or role.startswith('motif-')))
    # Merge chord voices and overlapping instrument attacks into a compact immutable profile.
    merged={}
    for event in events:
        key=round(event['Beat']*1000)
        if key not in merged or event['Strength']>merged[key]['Strength']:merged[key]=event
    held={}
    for hold in holds:
        key=round(hold['Beat']*1000)
        if key not in held or (hold['Melody'],hold['Beats'])>(held[key]['Melody'],held[key]['Beats']):held[key]=hold
    profile=dict(Version=2,Song=slug,Bpm=bpm,AudioSha256=hashlib.sha256(target.read_bytes()).hexdigest(),
        SourceMasterSha256=hashlib.sha256(source.read_bytes()).hexdigest(),SourceRevision=meta.get('revision',1),AudioProcessingVersion=2,
        SignatureSourceSha256=signature['source_sha256'],
        OffsetMs=500,EndBeat=blocks*32-1,Sections=sections,Events=sorted(merged.values(),key=lambda e:e['Beat']),Holds=sorted(held.values(),key=lambda e:e['Beat']))
    (output/'TrainingMusic'/f'{slug}-{bpm}.json').write_text(json.dumps(profile,separators=(',',':')))
    return f'{slug} {bpm}: {blocks*8} bars'

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--collection',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);parser.add_argument('--songs',nargs='*');parser.add_argument('--profiles-only',action='store_true')
    args=parser.parse_args()
    for path in ['Audio','TrainingMusic']:(args.output/path).mkdir(parents=True,exist_ok=True)
    tracks=json.loads((args.collection/'collection.json').read_text())
    if args.songs: tracks=[t for t in tracks if t['slug'] in args.songs]
    jobs=[(t,bpm) for t in tracks for bpm in TEMPOS]
    with concurrent.futures.ThreadPoolExecutor(max_workers=min(5,max(1,len(jobs)))) as pool:
        for result in pool.map(lambda job:export(args.collection,args.output,*job,profiles_only=args.profiles_only),jobs):print(result,flush=True)
if __name__=='__main__':main()
