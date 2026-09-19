import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import { spawn } from 'node:child_process';
import process from 'node:process';

import { MessageFlags, REST, Routes } from 'discord.js';
import ffmpegPath from 'ffmpeg-static';

import { voiceMetadataFromWav } from './voice-message.js';

const MAX_AUDIO_BYTES = 16 * 1024 * 1024;
const TEXT_CHANNEL_TYPES = new Set([0, 5, 10, 11, 12, 15]);

function argumentValue(args, name) {
  const index = args.indexOf(name);
  if (index < 0 || !args[index + 1]) throw new Error(`Missing ${name}`);
  return args[index + 1];
}

function sha256(buffer) {
  return createHash('sha256').update(buffer).digest('hex');
}

function assertWav(buffer, filePath) {
  if (!Buffer.isBuffer(buffer) || buffer.length < 12) {
    throw new Error(`Invalid WAV: ${filePath}`);
  }
  if (buffer.toString('ascii', 0, 4) !== 'RIFF' || buffer.toString('ascii', 8, 12) !== 'WAVE') {
    throw new Error(`Not a RIFF/WAVE file: ${filePath}`);
  }
  if (buffer.length > MAX_AUDIO_BYTES) {
    throw new Error(`WAV exceeds ${MAX_AUDIO_BYTES} bytes: ${filePath}`);
  }
}

function wavToOggOpus(wavBuffer) {
  return new Promise((resolve, reject) => {
    const binary = process.env.FFMPEG_PATH || ffmpegPath;
    if (!binary) {
      reject(new Error('ffmpeg binary not found'));
      return;
    }

    const child = spawn(binary, [
      '-loglevel', 'error',
      '-i', 'pipe:0',
      '-af', 'alimiter=limit=0.95',
      '-c:a', 'libopus',
      '-b:a', '64k',
      '-application', 'voip',
      '-ar', '48000',
      '-ac', '1',
      '-f', 'ogg',
      'pipe:1',
    ]);

    const chunks = [];
    let stderr = '';
    let settled = false;
    let timer;

    const fail = (error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(error);
    };

    timer = setTimeout(() => {
      child.kill();
      fail(new Error('ffmpeg timed out'));
    }, 30_000);

    child.stdout.on('data', (chunk) => chunks.push(chunk));
    child.stderr.on('data', (chunk) => {
      stderr = (stderr + chunk.toString()).slice(-2000);
    });
    child.on('error', fail);
    child.stdin.on('error', fail);
    child.on('close', (code) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      if (code === 0) {
        const output = Buffer.concat(chunks);
        if (output.length === 0) {
          reject(new Error('ffmpeg returned an empty OGG'));
        } else {
          resolve(output);
        }
      } else {
        reject(new Error(`ffmpeg exited ${code}: ${stderr.slice(0, 200)}`));
      }
    });

    child.stdin.end(wavBuffer);
  });
}

async function prepareVoice(filePath, description) {
  const wav = await fs.readFile(filePath);
  assertWav(wav, filePath);
  const meta = voiceMetadataFromWav(wav, 1);
  const ogg = await wavToOggOpus(wav);
  return {
    filePath,
    description,
    sourceWavBytes: wav.length,
    sourceWavSha256: sha256(wav),
    durationSecs: meta.durationSecs,
    waveform: meta.waveform,
    ogg,
  };
}

async function main() {
  const args = process.argv.slice(2);
  const tokenFile = argumentValue(args, '--token-file');
  const guildId = argumentValue(args, '--guild');
  const channelId = argumentValue(args, '--channel');
  const englishPath = argumentValue(args, '--english');
  const japanesePath = argumentValue(args, '--japanese');
  const dryRun = args.includes('--dry-run');

  const token = (await fs.readFile(tokenFile, 'utf8')).trim().replace(/^['"]|['"]$/g, '');
  if (!/^\S{50,}$/.test(token)) throw new Error('Token file did not contain a usable token');
  if (!/^\d{15,20}$/.test(guildId) || !/^\d{15,20}$/.test(channelId)) {
    throw new Error('Invalid Discord guild or channel ID');
  }

  const rest = new REST({ version: '10' }).setToken(token);
  const channel = await rest.get(Routes.channel(channelId));
  if (channel.guild_id !== guildId) throw new Error('Target channel belongs to a different guild');
  if (!TEXT_CHANNEL_TYPES.has(channel.type)) {
    throw new Error(`Target channel is not message-capable (type ${channel.type})`);
  }

  const candidates = [
    await prepareVoice(englishPath, 'Tsuki v19 staging | English target | English conditioning'),
    await prepareVoice(japanesePath, 'Tsuki v19 staging | Japanese target | Japanese conditioning'),
  ];

  for (const candidate of candidates) {
    const result = {
      dry_run: dryRun,
      channel_id: channel.id,
      channel_name: channel.name,
      description: candidate.description,
      source_wav: candidate.filePath,
      source_wav_bytes: candidate.sourceWavBytes,
      source_wav_sha256: candidate.sourceWavSha256,
      duration_secs: candidate.durationSecs,
      ogg_bytes: candidate.ogg.length,
    };

    if (!dryRun) {
      const message = await rest.post(Routes.channelMessages(channelId), {
        body: {
          flags: MessageFlags.IsVoiceMessage,
          attachments: [{
            id: 0,
            filename: 'voice-message.ogg',
            description: candidate.description,
            duration_secs: candidate.durationSecs,
            waveform: candidate.waveform,
          }],
        },
        files: [{ name: 'voice-message.ogg', data: candidate.ogg, contentType: 'audio/ogg' }],
        auth: true,
      });
      result.message_id = message.id;
      result.message_flags = message.flags;
      result.attachment_bytes = message.attachments?.[0]?.size ?? null;
      result.attachment_content_type = message.attachments?.[0]?.content_type ?? null;
    }

    console.log(JSON.stringify(result));
  }
}

main().catch((error) => {
  console.error(`send-v19-review failed: ${error.message}`);
  process.exitCode = 1;
});
