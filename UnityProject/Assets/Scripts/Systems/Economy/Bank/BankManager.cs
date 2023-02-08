using System.Collections.Generic;
using Shared.Managers;
using System.Security.Cryptography;

using System;

namespace Systems.Economy.Bank
{
	public class BankManager: SingletonManager<BankManager>
	{
		public List<PersonalAccount> Accounts { get; } = new();
		private Random random = new();

		public PersonalAccount CreateAccount(int pinNumber, string accountIdentifier, string accountHolder, int initialBalance = 0)
		{
			var account = new PersonalAccount(pinNumber, accountIdentifier, accountHolder, initialBalance);
			Accounts.Add(account);
			return account;
		}

		public PersonalAccount CreateAccount(string accountHolder)
		{
			var identifier = GenerateAccountIdentifier(accountHolder);
			var pin = random.Next(1000, 9999);
			return CreateAccount(pin, identifier, accountHolder);
		}

		/// <summary>
		/// Attempts to transfer money from one account to another. Meant for player to player transactions.
		/// </summary>
		/// <param name="from"></param>
		/// <param name="to"></param>
		/// <param name="amount"></param>
		/// <returns></returns>
		public bool TryTransfer(PersonalAccount from, PersonalAccount to, int amount)
		{
			if (from.TryWithDraw(amount) == false) return false;
			to.Deposit(amount);
			return true;
		}

		/// <summary>
		/// Like transfer, but the money expended doesn't go anywhere. Meant for taking money from players by the game itself, like taxes, NPCs, shops, etc.
		/// </summary>
		/// <param name="account">from what account</param>
		/// <param name="amount">the amount of money</param>
		/// <returns>True if the exchange was successful, otherwise false because the origin account didn't have enough funds or whatever other reason.</returns>
		public bool TryExpend(PersonalAccount account, int amount)
		{
			return account.TryWithDraw(amount);
		}

		/// <summary>
		/// Generates a random account number that is 7 digits long.
		/// </summary>
		/// <returns></returns>
		private string GenerateAccountIdentifier(string accountHolder)
		{
			return "pichula";
		}
	}
}