import { setIniValue } from '../backend/skyrim/gameIni';

/** The game's INIs edited one setting at a time, the rest of the file as it was. */

const INI = '[Display]\r\niSize W=1920\r\n\r\n[General]\r\n; a comment\r\nsLanguage=ENGLISH\r\n\r\n[Papyrus]\r\nbEnableLogging=0\r\n';

describe('setIniValue', () => {
    it('changes a setting where it is, whatever its letter case, and keeps the file\'s line ends', () => {
        const edit = setIniValue(INI, 'papyrus', 'BENABLELOGGING', '1');
        expect(edit).toMatchObject({ was: '0', changed: true });
        expect(edit.text).toBe(INI.replace('bEnableLogging=0', 'bEnableLogging=1'));
    });

    it('adds a new setting at the end of its section, before the empty line that parts it from the next', () => {
        const edit = setIniValue(INI, 'General', 'sStartingConsoleCommand', 'coc CrashTestCell');
        expect(edit.changed).toBe(true);
        expect(edit.was).toBeUndefined();
        expect(edit.text).toContain('sLanguage=ENGLISH\r\nsStartingConsoleCommand=coc CrashTestCell\r\n\r\n[Papyrus]');
    });

    it('adds a missing section at the end', () => {
        const edit = setIniValue(INI, 'Archive', 'bInvalidateOlderFiles', '1');
        expect(edit.text.endsWith('bEnableLogging=0\r\n\r\n[Archive]\r\nbInvalidateOlderFiles=1\r\n')).toBe(true);
    });

    it('removes a setting with null, and leaves a file alone that already says it', () => {
        expect(setIniValue(INI, 'General', 'sLanguage', null).text).not.toContain('sLanguage');
        expect(setIniValue(INI, 'General', 'sNothing', null)).toEqual({ text: INI, changed: false });
        expect(setIniValue(INI, 'General', 'sLanguage', 'ENGLISH')).toEqual({ text: INI, was: 'ENGLISH', changed: false });
    });

    it('never takes a comment or another section\'s key for the setting', () => {
        const text = '[General]\n;sLanguage=GERMAN\n[Other]\nsLanguage=FRENCH\n';
        const edit = setIniValue(text, 'General', 'sLanguage', 'ENGLISH');
        expect(edit.text).toBe('[General]\n;sLanguage=GERMAN\nsLanguage=ENGLISH\n[Other]\nsLanguage=FRENCH\n');
    });

    it('writes a new file', () => {
        expect(setIniValue('', 'General', 'sStartingConsoleCommand', 'coc Riverwood').text).toBe('[General]\nsStartingConsoleCommand=coc Riverwood\n');
    });
});
