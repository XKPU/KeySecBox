namespace KeySecBox;

public enum ErrorCodes
{
    Ok = 0,
    WrongPassword = 1,
    NoVault = 2,
    NotUnlocked = 3,
    IO = 4,
    NotFound = 5,
    Dup = 6,
    Legacy = 7,
    Generic = -1
}