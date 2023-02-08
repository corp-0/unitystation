namespace Systems.Economy.Bank
{
	public class PersonalAccount
	{
		public string AccountIdentifier { get; }
		public string AccountHolder { get; }
		public int PinNumber { get; }
		public int Balance { get; private set; }

		public PersonalAccount(int pinNumber, string accountHolder, string accountIdentifier, int balance = 0)
		{
			PinNumber = pinNumber;
			AccountIdentifier = accountIdentifier;
			AccountHolder = accountHolder;
			Balance = balance;
		}

		public void Deposit(int amount)
		{
			Balance += amount;
		}

		private void Withdraw(int amount)
		{
			Balance -= amount;
		}

		public bool TryWithDraw(int amount)
		{
			if (HasEnoughBalance(amount) == false) return false;
			Withdraw(amount);
			return true;
		}

		private bool HasEnoughBalance(int amount)
		{
			return Balance >= amount;
		}

	}
}