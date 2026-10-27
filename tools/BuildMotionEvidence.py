"""Compose local WPF captures into timed review GIFs and contact sheets."""
import argparse,json
from pathlib import Path
from PIL import Image,ImageDraw

cli=argparse.ArgumentParser()
cli.add_argument('--report',required=True)
cli.add_argument('--continuous-loop',action='store_true',help='Omit duplicate endpoint and inspection holds; preserve authored cycle duration')
cli.add_argument('--contact-sheet-only',action='store_true',help='Create still comparisons without implying that one-shot actions are loops')
args=cli.parse_args()
report=Path(args.report).resolve()
data=json.loads(report.read_text(encoding='utf-8'))
if 'clips' in data:
    # LiveMotionChecks stores actual WPF window captures with elapsed times.
    # Keep this separate from deterministic multi-angle pose samples below.
    for capture in data['clips']:
        name=capture['name'];images=[];times=[]
        for sample in capture['frames']:
            path=report.with_name(sample['image'])
            image=Image.open(path).convert('RGBA')
            frame=Image.new('RGBA',image.size,'#202632')
            frame.alpha_composite(image)
            ImageDraw.Draw(frame).text((8,8),f"{name}  {sample['seconds']:.2f}s  live WPF",fill='white')
            images.append(frame.convert('RGB'));times.append(float(sample['seconds']))
        if not images:continue
        intervals=[max(35,min(200,round((b-a)*1000))) for a,b in zip(times,times[1:])]
        duration=intervals+[intervals[-1] if intervals else 80]
        gif=report.with_name(f'{report.stem}.{name}.gif')
        if not args.contact_sheet_only:
            images[0].save(gif,save_all=True,append_images=images[1:],duration=duration,loop=0,optimize=False)
        indices=sorted(set(round(i*(len(images)-1)/8) for i in range(min(9,len(images)))))
        width,height=images[0].size
        sheet=Image.new('RGB',(width*3,height*3),'#202632')
        for cell,index in enumerate(indices):sheet.paste(images[index],((cell%3)*width,(cell//3)*height))
        sheet.save(report.with_name(f'{report.stem}.{name}.contact.jpg'),quality=92)
    print('Live WPF motion evidence created beside',report)
    raise SystemExit(0)
for capture in data['captures']:
    name=capture['action'];frames=[]
    for i in range(capture['frames']):
        pair=Image.new('RGB',(700,475),'#202632')
        for col,suffix in enumerate(('', '.side')):
            path=report.with_name(f'{report.stem}.{name}{suffix}.frame-{i:02}.png')
            image=Image.open(path).convert('RGBA')
            pair.paste(image,(col*350,0),image)
        ImageDraw.Draw(pair).text((10,455),f'{name}  {i/(capture["frames"]-1):.2f}   WPF sampled poses / front + side',fill='white')
        frames.append(pair)
    duration=round(capture['durationSeconds']*1000/(len(frames)-1))
    if args.contact_sheet_only:
        pass
    elif args.continuous_loop:
        loop_frames=frames[:-1]
        centiseconds=round(capture['durationSeconds']*100)
        boundaries=[round(i*centiseconds/len(loop_frames))*10 for i in range(len(loop_frames)+1)]
        intervals=[b-a for a,b in zip(boundaries,boundaries[1:])]
        loop_frames[0].save(report.with_name(f'{report.stem}.{name}.continuous.gif'),save_all=True,
                            append_images=loop_frames[1:],duration=intervals,loop=0)
    else:
        frames[0].save(report.with_name(f'{report.stem}.{name}.gif'),save_all=True,append_images=frames[1:],duration=[500]+[duration]*(len(frames)-2)+[500],loop=0)
    indices=[round(i*(len(frames)-1)/6) for i in range(7)]
    sheet=Image.new('RGB',(700*2,475*4),'#202632')
    for cell,index in enumerate(indices):sheet.paste(frames[index],((cell%2)*700,(cell//2)*475))
    sheet.save(report.with_name(f'{report.stem}.{name}.contact.jpg'),quality=92)
print('Motion evidence created beside',report)
