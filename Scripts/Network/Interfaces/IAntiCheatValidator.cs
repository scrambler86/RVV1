using Game.Networking.Adapters;

public interface IAntiCheatValidator
{
    bool ValidateInput(in AntiCheatInputContext context);
}
